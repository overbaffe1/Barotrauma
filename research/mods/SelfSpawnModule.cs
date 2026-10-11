// ============================================================================
//  SelfSpawnModule.cs — запуск второго СЕБЯ на сервере друга (волна 679)
//
//  ЧТО ДЕЛАЕТ: кнопка в CSHUB запускает ВТОРОЙ ПРОЦЕСС Barotrauma.exe с
//  флагами -connect + -username + -skipintro. Второй клиент заходит на ТОТ ЖЕ
//  сервер (Steam P2P по SteamId64 хоста или Lidgren IP:порт) с именем
//  "<твоё>_2". Сервер дубли-аккаунты не проверяет (волна 676), имена должны
//  различаться (NameTaken).
//
//  ТРЕБОВАНИЯ:
//   - ты подключён к серверу (модуль берёт endpoint из текущего пира)
//   - второй процесс = ~2 ГБ RAM
//   - если сервер с паролем — второй клиент спросит пароль сам
//   - CSHUB на втором клиенте не нужен (это обычный запуск игры),
//     но для модов сервера Steam их подтянет как обычно
//
//  ГЛАВНАЯ КНОПКА: «SPAWN SELF_2» — берёт GameMain.Client.ClientPeer
//  .ServerEndpoint (строку вида steam_7656119... или ip:port), твоё имя,
//  добавляет "_2" и стартует процесс.
//
//  ИСПОЛЬЗОВАНИЕ ДВОЙНОГО СЕБЯ (из волн 590/649/668):
//   - вот-лок: второй клиент поддерживает очередь голосований
//   - автобаланс-качели: два своих = дёргать составы PvP
//   - TransferVote: свой голос «ЗА»
//   - кик-воты: +1 голос
// ============================================================================

using System;
using System.Diagnostics;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class SelfSpawnModule : CSModuleBase
    {
        public override string Id   => "self_spawn";
        public override string Name => "Self Spawn";
        public override string Description =>
            "Launches a SECOND Barotrauma process that joins the SAME server as your clone " +
            "(name+\"_2\"). Server has no duplicate-account gate; names must differ. " +
            "P2P: uses host's SteamId64 endpoint; dedicated: ip:port.";
        public override string Category => "exploit";

        public override string GetLabel() => "Self Spawn 👥";

        public override void OnClick()
        {
            string endpoint = "";
            string myName = "Player";
            bool ok = false;

            try
            {
                var peer = GameMain.Client?.ClientPeer;
                if (peer?.ServerEndpoint != null)
                {
                    endpoint = peer.ServerEndpoint.StringRepresentation;
                    ok = true;
                }
            }
            catch { }

            try { myName = GameMain.Client?.Name ?? "Player"; } catch { }

            var box = new GUIMessageBox(
                "SELF SPAWN",
                (ok
                    ? "Current server endpoint:\n" + endpoint + "\n\n"
                    : "⚠ Не виден endpoint (не подключён?) — впишешь вручную.\n\n") +
                "Second client name: " + myName + "_2\n" +
                "(если занято — измени суффикс ниже)\n" +
                "\n" +
                "Запускается второй процесс игры с -connect. Steam-тикеты обоих валидны, " +
                "сервер дубли-аккаунты не проверяет.",
                new LocalizedString[]
                {
                    "SPAWN SELF_2",
                    "Close",
                },
                new Vector2(0.65f, 0.55f));

            box.Buttons[0].Color = Color.LimeGreen;
            box.Buttons[1].Color = Color.DarkGray;

            // вручную введённый endpoint/имя — через MessageBox нельзя, поэтому
            // просто два режима: авто (из текущего пира) и ручной (правь константы)
            const string ManualEndpoint = "";   // ← впиши "steam_76561198041382932" или "ip:port" если авто не сработал
            const string ManualName     = "";   // ← впиши имя второго вручную если надо

            box.Buttons[0].OnClicked = (_, _) =>
            {
                string ep = ok ? endpoint : ManualEndpoint;
                string baseName = string.IsNullOrWhiteSpace(ManualName) ? myName : ManualName;
                string secondName = baseName + "_2";

                if (string.IsNullOrWhiteSpace(ep))
                {
                    GUI.AddMessage("[SelfSpawn] endpoint пуст — подключись к серверу или впиши ManualEndpoint", Color.Red);
                    return true;
                }

                string result = SelfSpawnEngine.Spawn(ep, baseName, secondName);
                GUI.AddMessage("[SelfSpawn] " + result, Color.Orange);
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) => { box.Close(); return true; };
        }
    }

    internal static class SelfSpawnEngine
    {
        /// <summary>
        /// Запускает второй процесс Barotrauma с подключением к тому же серверу.
        /// endpoint — строка формата ConnectCommand: steam_7656... / ip:port / eos...
        /// </summary>
        public static string Spawn(string endpoint, string baseName, string secondName)
        {
            try
            {
                string exePath = typeof(GameMain).Assembly.Location;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "Barotrauma.exe";
                }

                // ConnectCommand.Parse: -connect "name" endpoint[,endpoint2...]
                // имя сервера — любое (используется только для отображения),
                // endpoint парсится по формату: steam_XXX → SteamP2P, ip:port → Lidgren
                string serverName = "friends_server";

                string args = $"-multiclienttestmode -connect \"{serverName}\" \"{endpoint}\" " +
                              $"-username \"{secondName}\" -skipintro";

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = true
                };

                Process.Start(psi);

                return $"второй процесс запущен: имя \"{secondName}\", endpoint \"{endpoint}\". " +
                       "Подожди загрузки (~20-40с). Если не зашёл — проверь суффикс имени (NameTaken).";
            }
            catch (Exception e)
            {
                return "fail: " + e.Message;
            }
        }
    }
}
// ============================================================================
//  ПРОВЕРКА (для твоего друга Hedrex | steam_76561198041382932):
//   1. Подключись к его серверу своим основным клиентом
//   2. CSHUB → Self Spawn → SPAWN SELF_2
//   3. Модуль возьмёт endpoint steam_76561198041382932 из твоего пира
//      (StringRepresentation у SteamP2PEndpoint = "steam_<id64>")
//   4. Второй процесс зайдёт как "<твоё имя>_2"
//
//  ЕСЛИ АВТО-ЭНДПОИНТ НЕ СРАБОТАЛ: впиши в константу ManualEndpoint в коде
//   ровно "steam_76561198041382932" и перекомпилируй (CSHUB перезагрузит).
//
//  ЧАСТЫЕ ГРАБЛИ:
//   - NameTaken: "_2" уже занят → поменяй суффикс в ManualName
//   - пароль на сервере: второй клиент сам спросит
//   - RAM: второй процесс ~2 ГБ
// ============================================================================
