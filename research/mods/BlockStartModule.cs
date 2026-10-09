using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  BLOCK START (волна 629) — On/Off тумблер блокировки старта.
    //
    //  Клик = toggle ON/OFF. ON запускает корутину которая каждый
    //  0.1с шлёт RESPONSE_CANCEL_STARTGAME (1 байт header).
    //  Сервер: если isRoundStartWarningActive → AbortStartGame →
    //  старт отменён для ВСЕХ. Работает в кампании с перками.
    //
    //  Не отключается при старте/выходе в лобби/респавне.
    //  Отключается ТОЛЬКО повторным кликом (OFF).
    // ============================================================
    public class BlockStartModule : CSModuleBase
    {
        public override string Id   => "block_start";
        public override string Name => "Block Start";
        public override string Description =>
            "🚫 ON/OFF: блокирует старт раунда.\n\n" +
            "Клик = toggle. ВКЛ запускает корутину которая\n" +
            "каждые 0.1с шлёт CANCEL_STARTGAME серверу.\n" +
            "Сервер отменяет старт для ВСЕХ при warning.\n\n" +
            "• Не отключается после старта/лобби\n" +
            "• Отключается ТОЛЬКО повторным кликом\n" +
            "• Работает в кампании с перками\n" +
            "• SANDBOX без перков = старт мгновенный (бесполезно)";

        public override string Category => "exploit";

        private static bool _enabled = false;
        private static int _cancelCount = 0;
        private static readonly Color OnColor  = new Color(255, 100, 100);
        private static readonly Color OffColor = new Color(100, 200, 100);
        private static readonly Color InfoColor= new Color(255, 170, 90);

        public override string GetLabel()
        {
            if (_enabled)
            {
                string status = GameMain.Client?.GameStarted == true ? "раунд" : "лобби";
                return $"🚫 Block Start [ON·{status}] {_cancelCount}";
            }
            return "🚫 Block Start [OFF]";
        }

        public override void OnClick()
        {
            if (GameMain.Client?.ClientPeer == null)
            {
                GUI.AddMessage("[BlockStart] Нет подключения", Color.Red);
                return;
            }

            _enabled = !_enabled;

            if (_enabled)
            {
                _cancelCount = 0;
                GUI.AddMessage("[BlockStart] 🚫 ВКЛЮЧЁН — старт блокируется", OnColor);
                GUI.AddMessage("[BlockStart] Работает в кампании (15с окно warning)", InfoColor);
                CoroutineManager.StartCoroutine(BlockLoop());
            }
            else
            {
                GUI.AddMessage("[BlockStart] ВЫКЛЮЧЕН", OffColor);
                // корутина сама завершится (yield break при !_enabled)
            }
        }

        private static IEnumerable<CoroutineStatus> BlockLoop()
        {
            while (_enabled)
            {
                // Проверяем подключение
                if (GameMain.Client?.ClientPeer == null)
                {
                    _enabled = false;
                    GUI.AddMessage("[BlockStart] Подключение потеряно", Color.Red);
                    yield break;
                }

                try
                {
                    // 1 байт header — RESPONSE_CANCEL_STARTGAME
                    IWriteMessage msg = new WriteOnlyMessage();
                    msg.WriteByte((byte)ClientPacketHeader.RESPONSE_CANCEL_STARTGAME);
                    GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                    _cancelCount++;
                }
                catch
                {
                    // не критично, попробуем в следующий раз
                }

                // 10 пакетов/сек достаточно (warning длится 15с = 150 пакетов)
                double until = Timing.TotalTime + 0.1;
                while (Timing.TotalTime < until)
                {
                    if (!_enabled) { yield break; }
                    yield return CoroutineStatus.Running;
                }
            }
            yield return CoroutineStatus.Success;
        }

        public override void Dispose()
        {
            _enabled = false;
            base.Dispose();
        }
    }
}
