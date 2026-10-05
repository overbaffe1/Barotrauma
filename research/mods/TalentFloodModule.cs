using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  TALENT FLOOD (волна 579) — ★★ НЕТ ПРОВЕРКИ ОЧКОВ
    //  UpdateTalents пакет: CanManageTalents = owner-check (для себя true),
    //  IsViableTalentForCharacter = только prerequisite/blockedTrees,
    //  GetAvailableTalentPoints НЕ вызывается на сервере.
    //  Отправляем ВСЁ дерево нашего job'а в prerequisite-порядке →
    //  все таланты разблокированы БЕСПЛАТНО.
    //  Работает в раунде, не только в лобби!
    //  EventType.UpdateTalents = 11 (CharacterEventData.cs:25)
    //  TalentTree/TalentPrefab internal — доступ через TalentTree.IsViable
    //  (public static, TalentTree.cs:60) + TalentPrefab.TalentPrefabs (public)
    // ============================================================
    public class TalentFloodModule : CSModuleBase
    {
        public override string Id   => "talent_flood";
        public override string Name => "Talent Flood";
        public override string Description =>
            "★ Разблокировать ВСЕ таланты БЕЗ очков.\n\n" +
            "• UpdateTalents packet не проверяет GetAvailableTalentPoints\n" +
            "• Только prerequisite-порядок (модуль сортирует через TalentTree)\n" +
            "• Работает в раунде и в лобби\n" +
            "• Все статы/бонусы/эффекты активируются мгновенно";
        public override string Category => "exploit";

        private static readonly Color AccentColor = new Color(255, 220, 100);

        public override string GetLabel() => "Talent Flood ⭐";

        public override void OnClick()
        {
            if (GameMain.Client == null || Character.Controlled?.Info == null)
            {
                GUI.AddMessage("[TalentFlood] Нужен персонаж", Color.Orange);
                return;
            }

            var info = Character.Controlled.Info;
            var jobPrefab = info.Job?.Prefab;
            if (jobPrefab == null)
            {
                GUI.AddMessage("[TalentFlood] Нет профессии", Color.Orange);
                return;
            }

            // TalentTree — internal но TalentTree.JobTalentTrees public static,
            // TryGet возвращает TalentTree с public AllTalentIdentifiers
            if (!TalentTree.JobTalentTrees.TryGet(jobPrefab.Identifier, out var tree))
            {
                GUI.AddMessage("[TalentFlood] TalentTree не найден для " + jobPrefab.Identifier, Color.Red);
                return;
            }

            var allIds = new List<Identifier>(tree.AllTalentIdentifiers);
            if (allIds.Count == 0)
            {
                GUI.AddMessage("[TalentFlood] Дерево пустое", Color.Orange);
                return;
            }

            // Multi-pass prerequisite-сортировка через публичный TalentTree.IsViableTalentForCharacter
            var ordered = new List<Identifier>();
            var remaining = new List<Identifier>(allIds);

            for (int pass = 0; pass < 20 && remaining.Count > 0; pass++)
            {
                var stillRemaining = new List<Identifier>();
                foreach (var talentId in remaining)
                {
                    if (TalentTree.IsViableTalentForCharacter(Character.Controlled, talentId, ordered))
                    {
                        ordered.Add(talentId);
                    }
                    else
                    {
                        stillRemaining.Add(talentId);
                    }
                }
                if (stillRemaining.Count == remaining.Count)
                {
                    ordered.AddRange(stillRemaining); // остаток добавляем силой
                    break;
                }
                remaining = stillRemaining;
            }

            try
            {
                SendUpdateTalents(ordered);
                GUI.AddMessage("[TalentFlood] ⭐ Отправлено " + ordered.Count +
                    " талантов для " + info.Name, AccentColor);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[TalentFlood] фейл: " + e.Message, Color.Red);
            }
        }

        // Пакет UpdateTalents (CharacterNetworking.cs:308):
        // EventType.UpdateTalents = 11 (CharacterEventData.cs:25)
        // u16 talentCount + u32 uintIdentifier × N
        private static void SendUpdateTalents(List<Identifier> talentIds)
        {
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { return; }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);

            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.CharacterInput);

                msg.WriteRangedInteger(11, 0, 15); // EventType.UpdateTalents
                msg.WriteUInt16((ushort)talentIds.Count);
                foreach (var id in talentIds)
                {
                    TalentPrefab prefab = null;
                    foreach (var p in TalentPrefab.TalentPrefabs)
                    {
                        if (p.Identifier == id) { prefab = p; break; }
                    }
                    msg.WriteUInt32(prefab?.UintIdentifier ?? 0);
                }
            }

            peer.Send(msg, DeliveryMethod.Reliable);
        }

        public override void Dispose() { base.Dispose(); }
    }
}
