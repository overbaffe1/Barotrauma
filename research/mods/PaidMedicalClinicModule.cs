using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  ПЛАТНАЯ медклиника — ОТДЕЛЬНЫЙ файл, имена классов УНИКАЛЬНЫ
    //  (PaidMedicalClinicExploit вместо MedicalClinicExploit),
    //  чтобы не конфликтовать со старым FreeMedicalClinicModule.cs.
    //
    //  Сервер при HEAL_PENDING: totalCost = сумма ВСЕХ Price
    //  → TryPurchase(client, totalCost) → если денег меньше —
    //  InsufficientFunds, лечение ОТКЛОНЯЕТСЯ целиком.
    //  Платишь за КАЖДУЮ строку (включая аффликции, которых на теле нет).
    //  Ваниль: ~180+4 строк × 100 = ~18 400 mk за одного персонажа.
    // ============================================================
    public class PaidMedicalClinicModule : CSModuleBase
    {
        public override string Id   => "paid_medical_clinic";
        public override string Name => "Paid Medical Clinic";
        public override string Description =>
            "Same clinic packet as Free Medical Clinic, but Price = " +
            PaidMedicalClinicExploit.PricePerAffliction + " per affliction entry — spends your money.";
        public override string Category => "exploit";

        public override string GetLabel() => "Paid Medical Clinic";

        public override void OnClick()
        {
            int entries = AfflictionPrefab.List
                .Count(p => p != null && !p.Identifier.IsEmpty && p.MaxStrength > 0) + 4;
            int approxCost = entries * PaidMedicalClinicExploit.PricePerAffliction;

            var box = new GUIMessageBox(
                "PAID MEDICAL CLINIC",
                "Sends ADD_PENDING + HEAL_PENDING with Price = " +
                    PaidMedicalClinicExploit.PricePerAffliction + " per entry.\n" +
                "Server sums ALL prices and TryPurchase()s it — must be affordable,\n" +
                "otherwise InsufficientFunds and NOTHING is healed.\n" +
                "\n" +
                "Approx cost per heal: ~" + entries + " entries x " +
                    PaidMedicalClinicExploit.PricePerAffliction + " = ~" + approxCost + " mk per person.\n" +
                "\n" +
                "Requirements:\n" +
                " - MultiPlayerCampaign, outpost with a medical NPC\n" +
                " - Outpost not in combat (IsOutpostInCombat)\n" +
                " - Clinic within 250 units or AllowRemoteCampaignInteractions=true\n" +
                " - Enough money in your wallet\n" +
                "\n" +
                "Only Team1 characters from GetCrewCharacters() are healed (incl. bots).",
                new LocalizedString[]
                {
                    "Heal Self",
                    "Heal Whole Crew",
                    "Close",
                },
                new Vector2(0.6f, 0.7f));

            box.Buttons[0].Color = Color.Orange;
            box.Buttons[1].Color = Color.CornflowerBlue;
            box.Buttons[2].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                if (Character.Controlled?.Info != null)
                    PaidMedicalClinicExploit.Heal(new[] { Character.Controlled.Info });
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                var crew = GameSession.GetSessionCrewCharacters(CharacterType.Both)
                    .Where(c => c?.Info != null)
                    .Select(c => c.Info)
                    .ToImmutableArray();
                PaidMedicalClinicExploit.Heal(crew);
                return true;
            };

            box.Buttons[2].OnClicked = (_, _) => { box.Close(); return true; };
        }
    }

    // ============================================================
    //  Хоткей-версия: лечит того, кто под курсором (за деньги)
    // ============================================================
    public class PaidHealCursorModule : CSModuleBase
    {
        public override string Id   => "paid_heal_under_cursor";
        public override string Name => "Paid Heal Under Cursor";
        public override string Description =>
            "Heals the Team1 crewmember under the mouse cursor, paying " +
            PaidMedicalClinicExploit.PricePerAffliction + " per affliction entry.";
        public override string Category => "exploit";

        private const float MaxPickDistance = 220f;

        public override string GetLabel() => "Paid Heal Under Cursor";

        public override void OnClick()
        {
            Character target = FindCharacterUnderCursor(MaxPickDistance);
            if (target?.Info == null) return;

            // Медклиника лечит только Team1, на остальных пакет проглотится вхолостую
            if (target.TeamID != CharacterTeamType.Team1) return;

            PaidMedicalClinicExploit.Heal(new[] { target.Info });
        }

        private static Character FindCharacterUnderCursor(float maxDist)
        {
            Camera cam = (Screen.Selected as GameScreen)?.Cam;
            if (cam == null) return null;

            Vector2 mouseScreen = PlayerInput.MousePosition;
            Vector2 mouseWorld  = cam.ScreenToWorld(mouseScreen);

            Character best = null;
            float bestDist = maxDist * maxDist;

            foreach (var c in Character.CharacterList)
            {
                if (c == null || c.Removed || c.Info == null || c.IsDead) continue;

                float d = Vector2.DistanceSquared(mouseWorld, c.WorldPosition);

                if (c.AnimController?.Limbs != null)
                {
                    foreach (var limb in c.AnimController.Limbs)
                    {
                        if (limb?.body == null) continue;
                        float ld = Vector2.DistanceSquared(mouseWorld, limb.body.SimPosition);
                        if (ld < d) d = ld;
                    }
                }

                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }

            return best;
        }
    }

    // ============================================================
    //  Общая логика (reflection + сетевые пакеты), Price = константа.
    //  Имя класса УНИКАЛЬНОЕ: PaidMedicalClinicExploit.
    // ============================================================
    internal static class PaidMedicalClinicExploit
    {
        /// <summary>Цена ОДНОЙ строки аффликции в пакете (0..65535).</summary>
        public const ushort PricePerAffliction = 100;

        private static Type   _crewType, _affType;
        private static FieldInfo _crewId, _crewAff, _affId, _affStr, _affVit, _affPrice;
        private static MethodInfo _write, _createRange;
        private static bool _ready;

        private static void LogErr(string m) =>
            LuaCsLogger.LogError("[PaidMedClinic] " + m);

        private static void EnsureReflection()
        {
            if (_ready) return;

            _crewType = typeof(Character).Assembly.GetType("Barotrauma.MedicalClinic+NetCrewMember");
            _affType  = typeof(Character).Assembly.GetType("Barotrauma.MedicalClinic+NetAffliction");

            if (_crewType != null)
            {
                _crewId  = _crewType.GetField("CharacterInfoID", BindingFlags.Instance | BindingFlags.Public);
                _crewAff = _crewType.GetField("Afflictions",     BindingFlags.Instance | BindingFlags.Public);
            }
            if (_affType != null)
            {
                _affId    = _affType.GetField("Identifier",       BindingFlags.Instance | BindingFlags.Public);
                _affStr   = _affType.GetField("Strength",         BindingFlags.Instance | BindingFlags.Public);
                _affVit   = _affType.GetField("VitalityDecrease", BindingFlags.Instance | BindingFlags.Public);
                _affPrice = _affType.GetField("Price",            BindingFlags.Instance | BindingFlags.Public);
            }
            _write = typeof(INetSerializableStruct).GetMethod(
                "Write", BindingFlags.Instance | BindingFlags.Public,
                binder: null, types: new[] { typeof(IWriteMessage) }, modifiers: null);

            // ImmutableArray.CreateRange<T>(IEnumerable<T>) — принимает T[]
            _createRange = typeof(ImmutableArray)
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .First(m => m.Name == "CreateRange"
                            && m.IsGenericMethodDefinition
                            && m.GetParameters().Length == 1
                            && m.GetParameters()[0].ParameterType.IsGenericType
                            && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.MakeGenericMethod(_affType);

            _ready = _crewType != null && _affType != null &&
                     _crewId != null && _crewAff != null &&
                     _affId != null && _affPrice != null &&
                     _write != null && _createRange != null;
        }

        public static void Heal(IReadOnlyList<CharacterInfo> targets)
        {
            if (targets == null || targets.Count == 0) return;
            if (GameMain.IsSingleplayer) return;

            EnsureReflection();
            if (!_ready) { LogErr("Reflection not ready."); return; }

            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { LogErr("ClientPeer is null."); return; }

            Identifier[] ids = AfflictionPrefab.List
                .Where(p => p != null && !p.Identifier.IsEmpty && p.MaxStrength > 0)
                .Select(p => p.Identifier)
                .Concat(new[]
                {
                    "damage".ToIdentifier(),
                    "burn".ToIdentifier(),
                    "bleeding".ToIdentifier(),
                    "internaldamage".ToIdentifier(),
                })
                .Distinct()
                .ToArray();

            foreach (var info in targets)
            {
                if (info == null) continue;

                Array arr = Array.CreateInstance(_affType, ids.Length);
                for (int i = 0; i < ids.Length; i++)
                {
                    object aff = Activator.CreateInstance(_affType);
                    _affId    ?.SetValue(aff, ids[i]);
                    _affStr   ?.SetValue(aff, (ushort)0);
                    _affVit   ?.SetValue(aff, 0);
                    _affPrice ?.SetValue(aff, PricePerAffliction); // ← ПЛАТНО: 100 за строку
                    arr.SetValue(aff, i);
                }

                object immutable = _createRange.Invoke(null, new object[] { arr });

                // NetCrewMember — record struct, работает через boxed-копию
                object crew = Activator.CreateInstance(_crewType);
                _crewId .SetValue(crew, info.ID);
                _crewAff.SetValue(crew, immutable);

                var add = new WriteOnlyMessage();
                add.WriteByte((byte)ClientPacketHeader.MEDICAL);
                add.WriteByte(4); // ADD_PENDING
                _write.Invoke(crew, new object[] { add });
                peer.Send(add, DeliveryMethod.Reliable);
            }

            var heal = new WriteOnlyMessage();
            heal.WriteByte((byte)ClientPacketHeader.MEDICAL);
            heal.WriteByte(7); // HEAL_PENDING
            peer.Send(heal, DeliveryMethod.Reliable);
        }
    }
}
