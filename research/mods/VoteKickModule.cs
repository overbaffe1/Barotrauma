// ============================================================================
//  VoteKickModule.cs — «ВОТЧИНА КИКА» (волна 721)
//  ЕДИНСТВЕННЫЙ БЕЗПЕРМОВЫЙ способ кикнуть ДРУГОГО игрока — голосование.
//  Модуль автоматизирует пакет и показывает математику.
//
//  МЕХАНИКА (GameServer.UpdateVoteStatus, выполняется при каждом голосе):
//    minimumKickVotes = Math.Max(2, eligibleCount * KickVoteRequiredRatio(0.6))
//    цель кикается/банится если: не OwnerConnection + НЕТ пермов Kick/Ban/Unban
//                               + KickVoteCount >= minimumKickVotes
//    → BanClient(c, "KickedByVoteAutoBan", AutoBanTime = 3600с = 1 ЧАС!)
//  Итого: на 2-3 игрока = 2 голоса, на 4 = 3 (4×0.6=2.4→max(2,2.4)=2.4 → 3?:
//  KickVoteCount целое >= 2.4 = 3), на 6 = 4 голоса.
//
//  УСЛОВИЯ ДЛЯ ГОЛОСУЮЩЕГО (Voting.ServerRead, case Kick):
//    - ServerSettings.AllowVoteKick = true (ДЕФОЛТ true!)
//    - ты на сервере > DisallowKickVoteTime (дефолт 120с)
//    - 1 голос на цель (HasKickVoteFrom не даст проголосовать дважды)
//
//  ФАКТЫ:
//    - голоса ЦЕЛИ ПЕРСИСТЯТ через её реконнект (previousPlayer.KickVoters
//      восстанавливаются при релогине, GameServer:319) — не слить голоса
//      перезаходом, анти-эвейд по дизайну
//    - цель с KickVoteCount>0 НЕ МОЖЕТ сменить ник (GameServer:3542)
//    - итоговая трансляция: "X исключён/забанен. Причина: KickedByVoteAutoBan"
//      (фиксированная локаль, ИНЪЕКЦИИ ТЕКСТА ЗДЕСЬ НЕТ)
//    - самоголосование разрешено (проверки sender!=kicked НЕТ) — цель может
//      забанить себя сама на час (побочный прикол)
//
//  ПАКЕТ: UPDATE_LOBBY(0) + SegmentTable + Vote-сегмент { byte 4 (VoteType.Kick),
//  byte targetSessionId }. VoteType/ClientNetSegment internal → литералы/рефлексия.
// ============================================================================

using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class VoteKickModule : CSModuleBase
    {
        public override string Id   => "vote_kick";
        public override string Name => "Vote Kick";
        public override string Description =>
            "The ONLY permissionless way to kick ANOTHER player: the kick vote. Min 2 votes " +
            "(max(2, eligible*0.6)), result = 1 HOUR AUTO-BAN. Owner and Kick/Ban/Unban perm-holders " +
            "immune. You must be 120s+ on the server. This module lists players and sends the vote packet.";
        public override string Category => "exploit";

        public override string GetLabel() => "Vote Kick 🥾";

        private static GUIMessageBox _box;
        private static GUILayoutGroup _listGroup;
        private static GUITextBlock _status;

        public override void OnClick()
        {
            if (_box != null) { _box.Close(); _box = null; return; }
            Open();
        }

        private static void Open()
        {
            if (GameMain.Client?.ClientPeer == null)
            {
                GUI.AddMessage("[VoteKick] нет подключения", Color.Red);
                return;
            }

            _box = new GUIMessageBox(
                headerText: "VOTE KICK — вотчина (кик другого)",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.66f, 0.6f));

            var content = _box.Content;
            content.ClearChildren();

            new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.2f), content.RectTransform),
                "Минимум 2 голоса (max(2, игроки×0.6)). Результат = АВТОБАН 1 ЧАС.\n" +
                "Оунер и админы (Kick/Ban пермы) защищены. Ты должен быть на сервере 120с+.\n" +
                "Голоса цели персистят через её релогин — не слить.",
                textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont)
            { TextColor = new Color(255, 170, 120) };

            _listGroup = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.62f), content.RectTransform),
                isHorizontal: false);
            _listGroup.RelativeSpacing = 0.008f;

            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.16f), content.RectTransform),
                "Выбери цель:", wrap: true, font: GUIStyle.SmallFont);
            _status.TextColor = new Color(120, 220, 255);

            FillList();
        }

        private static void FillList()
        {
            if (_listGroup == null) { return; }
            _listGroup.ClearChildren();

            var me = GameMain.Client;
            int count = 0;
            foreach (Client c in GameMain.Client.ConnectedClients)
            {
                if (c == null) { continue; }
                count++;
                bool isMe = (me != null && c.SessionId == me.SessionId);
                string badge = c.IsOwner ? " [ОУНЕР]" : (c.HasPermissions ? " [АДМИН?]" : "");
                string label = $"{c.Name}{badge}" + (isMe ? " (это ты)" : "");

                var row = new GUILayoutGroup(
                    new RectTransform(new Vector2(1f, 0.14f), _listGroup.RectTransform),
                    isHorizontal: true);
                row.RelativeSpacing = 0.01f;

                new GUITextBlock(
                    new RectTransform(new Vector2(0.65f, 1f), row.RectTransform),
                    label, textAlignment: Alignment.CenterLeft)
                { CanBeFocused = false, TextColor = isMe ? Color.Gray : Color.White };

                var btn = new GUIButton(
                    new RectTransform(new Vector2(0.35f, 1f), row.RectTransform),
                    isMe ? "кикнуть себя" : "ГОЛОСОВАТЬ");
                btn.Color = isMe ? new Color(90, 90, 90) : new Color(170, 60, 50);
                Client target = c;
                btn.OnClicked = (b, d) =>
                {
                    SendKickVote(target.SessionId);
                    string res = isMe
                        ? "самоголосование отправлено (проверка sender!=kicked ОТСУТСТВУЕТ — забанишь себя на час)"
                        : $"голос против \"{target.Name}\" отправлен. Нужно ещё голосов: минимум 2 всего.";
                    if (_status != null) { _status.Text = "→ " + res; }
                    GUI.AddMessage("[VoteKick] " + res, Color.Orange);
                    return true;
                };
            }

            if (count == 0)
            {
                new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.2f), _listGroup.RectTransform),
                    "Список игроков пуст/не получен.", textColor: Color.Gray);
            }
        }

        private static void SendKickVote(byte targetSessionId)
        {
            try
            {
                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY); // == 0

                // --- сегмент-таблица (как в EventBomb) ---
                int pointerLocation = msg.BitPosition;
                msg.WriteInt32(0); // placeholder

                int segmentStart = msg.BitPosition;

                // Vote-сегмент: byte voteType(4=Kick) + byte targetSessionId
                msg.WriteByte(4); // VoteType.Kick (internal enum → литерал)
                msg.WriteByte(targetSessionId);

                int tablePosition = msg.BitPosition;
                msg.BitPosition = pointerLocation;
                msg.WriteInt32(tablePosition - pointerLocation);
                msg.BitPosition = tablePosition;

                msg.WriteUInt16(1); // segment count

                var asm = typeof(GameMain).Assembly;
                var tSeg = asm.GetType("Barotrauma.Networking.ClientNetSegment", false);
                var tSegmentOpen = asm.GetType("Barotrauma.Networking.Segment`1", false);
                if (tSeg == null || tSegmentOpen == null)
                {
                    GUI.AddMessage("[VoteKick] reflection fail", Color.Red);
                    return;
                }
                var tSegment = tSegmentOpen.MakeGenericType(tSeg);
                var ci = tSegment.GetConstructor(new[] { tSeg, typeof(int) });
                object segment = ci.Invoke(new[] { Enum.Parse(tSeg, "Vote"), (object)(segmentStart - pointerLocation) });
                typeof(INetSerializableStruct).GetMethod("Write").Invoke(segment, new object[] { msg });

                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[VoteKick] send fail: " + e.Message, Color.Red);
            }
        }
    }
}
