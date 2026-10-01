// ============================================================================
//  FreeHealModule.cs — CSHUB module ("Free Medical Clinic")
//
//  ЧТО ЭТО:
//  Клиентский модуль для демонстрации доверительной границы MedicalClinic
//  (волна 453). Клиника принимает ОТ КЛИЕНТА структуру NetCrewMember с
//  полем Price — и платит именно эту сумму. Собираем список "аффликций" с
//  Price=0 и триггерим лечение: totalCost = Σ Price = 0 → TryPurchase(0)
//  возвращает true → сервер снимает перечисленные аффликции до нуля.
//
//  ГЕЙТЫ СЕРВЕРА (все обходимы/мягкие):
//    - rate limiter 20 запросов / 5 сек (наш мод шлёт 2 пакета за раз — ок);
//    - IsOutpostInCombat(): ВНЕ АУТПОСТА всегда false (MedicalClinic.cs:172:
//      "if (Level.Loaded is not Outpost) return false;") → лечимся где угодно:
//      посреди раунда, на вреке, в бездне;
//    - TryPurchase(0): price == 0 → return true без списаний;
//    - цель ищется в GetCrewCharacters() = TeamID==Team1 по CharacterInfoID —
//      можно лечить ЛЮБОГО тиммейта (Free Medic), не только себя.
//
//  ЧТО ЛЕЧИМ (Identifier любого аффликциона — сервер зовёт
//  ReduceAfflictionOnAllLimbs(identifier, MaxStrength)):
//    internaldamage, blunttrauma, lacerations, bitewounds, hemorrhage,
//    opioidoverdose, alk overdose... + huskinfection (в ваниле лечится
//    только калемс-антибиотиками!) + ЛЮБЫЕ ПОЗИТИВНЫЕ аффликции
//    (калемс-тоник, эндорфиновый буст) = гриф снятием баффов.
//
//  ПАКЕТЫ (два, порядок сверен с MedicalClinic.ServerRead, волна 453):
//    #1: u8 17 (ClientPacketHeader.MEDICAL)
//        u8 4  (NetworkHeader.ADD_PENDING)
//        WriteNetSerializableStruct(new NetCrewMember {
//            CharacterInfoID = <id цели>,
//            Afflictions = [ { Identifier, Strength, VitalityDecrease=0, Price=0 } ]
//        })
//        # структура кладётся в PendingHeals КАК ЕСТЬ, цена не пересчитывается
//    #2: u8 17 (ClientPacketHeader.MEDICAL)
//        u8 7  (NetworkHeader.HEAL_PENDING)
//        # → HealAllPending: totalCost=0 → paid → ReduceAfflictionOnAllLimbs
//
//  ЗАМЕТКИ:
//    - WriteNetSerializableStruct — extension на IWriteMessage
//      (NetworkExtensions.cs:22), мод зовёт напрямую;
//    - NetCrewMember/NetAffliction — public-структуры MedicalClinic (shared);
//    - серверная серриализация цену НЕ трогает: SetAffliction вызывается
//      только на серверном пути построения списков — клиентский путь её минует;
//    - если цель уже в PendingHeals — InsertPendingCrewMember перезапишет
//      запись по CharacterEquals (CharacterInfoID).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Extensions;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

// NOTE: неймспейс/базовый класс приведи в соответствие со своей сборкой мода
// (так же, как SellByIdModule наследует CSModuleBase).

public sealed class FreeHealModule
{
    // ---------------------------- константы пакета --------------------------

    private const byte PacketHeaderMedical = 17;   // ClientPacketHeader.MEDICAL
    private const byte HeaderAddPending = 4;       // MedicalClinic.NetworkHeader.ADD_PENDING
    private const byte HeaderHealPending = 7;      // MedicalClinic.NetworkHeader.HEAL_PENDING

    // дефолтный набор: всё, что обычно болит (+ husk)
    private static readonly (string Id, ushort Strength)[] DefaultAfflictions =
    {
        ("internaldamage",  100),
        ("blunttrauma",     100),
        ("lacerations",     100),
        ("bitewounds",      100),
        ("hemorrhage",      100),
        ("huskinfection",   100),
        ("oxygenlow",       100),
        ("opiateoverdose",  100),
    };

    private const double SendCooldown = 1.0; // клиника: 20 req/5s — нам хватит
    private static double _lastSendTime = -999.0;

    private static MethodInfo _sendMethod;
    private static bool _sendMethodResolved;

    // ------------------------------- публичное ------------------------------

    /// <summary>Полное лечение КОНКРЕТНОГО персонажа (по умолчанию — себя).</summary>
    public static void Heal(Character target, IEnumerable<(string Id, ushort Strength)> extraAfflictions = null)
    {
        try
        {
            if (GameMain.Client == null || GameMain.GameSession?.Campaign is not MultiPlayerCampaign)
            {
                GUI.AddMessage("FreeHeal: нужен мультиплеер-кампейн", DangerColor);
                return;
            }

            var info = target?.Info;
            if (info == null)
            {
                GUI.AddMessage("FreeHeal: у цели нет CharacterInfo", DangerColor);
                return;
            }

            // 1) ADD_PENDING: заявляем лечение с Price=0
            var afflictions =
                (extraAfflictions ?? DefaultAfflictions)
                .Select(a => new MedicalClinic.NetAffliction
                {
                    Identifier = a.Id.ToIdentifier(),
                    Strength = a.Strength,
                    VitalityDecrease = 0,
                    Price = 0                       // ← вся соль: платим ноль
                })
                .ToImmutableArray();

            var crewMember = new MedicalClinic.NetCrewMember
            {
                CharacterInfoID = info.ID,
                Afflictions = afflictions
            };

            var addMsg = CreateMsg();
            addMsg.WriteByte(PacketHeaderMedical);
            addMsg.WriteByte(HeaderAddPending);
            addMsg.WriteNetSerializableStruct(crewMember);
            Send(addMsg, $"ADD_PENDING ({afflictions.Length} аффликций, цена 0)");

            // 2) HEAL_PENDING: "оплати и вылечи"
            var healMsg = CreateMsg();
            healMsg.WriteByte(PacketHeaderMedical);
            healMsg.WriteByte(HeaderHealPending);
            Send(healMsg, "HEAL_PENDING (сумма 0 → бесплатно)");

            GUI.AddMessage($"FreeHeal: заявка на {info.Name} отправлена", AccentColor);
        }
        catch (Exception e)
        {
            LuaCsLogger.LogError("[FreeHeal] Heal: " + e);
            GUI.AddMessage("FreeHeal: ошибка — " + e.Message, DangerColor);
        }
    }

    /// <summary>Лечит себя стандартным набором.</summary>
    public static void HealSelf() => Heal(Character.Controlled);

    /// <summary>Лечит ЦЕЛЬ ПРИЦЕЛА (Free Medic). Team1-only на сервере.</summary>
    public static void HealFocused()
    {
        var focus = Character.Controlled?.FocusedCharacter ?? Character.Controlled?.SelectedCharacter;
        if (focus == null) { HealSelf(); return; }
        Heal(focus);
    }

    /// <summary>
    /// Гриф-режим: снимает ПОЗИТИВНЫЕ аффликции у цели (тоник/буст).
    /// Карма этого НЕ видит (клиника минует OnCharacterHealthChanged).
    /// </summary>
    public static void StripFocused()
    {
        var focus = Character.Controlled?.FocusedCharacter ?? Character.Controlled?.SelectedCharacter;
        if (focus?.Info == null)
        {
            GUI.AddMessage("FreeHeal: нет цели под прицелом", DangerColor);
            return;
        }

        var positive = new (string, ushort)[]
        {
            ("calystiumtoniceffect", 100),   // калемс-тоник
            ("tonuseffect",          100),
            ("endocrineboosterpreservation", 100),
            ("endocrineboosteracceleration", 100),
            ("hypersensitivity",     100),
            ("phalanx",              100)
        };
        Heal(focus, positive);
    }

    // ------------------------------ транспорт -------------------------------

    private static void Send(IWriteMessage msg, string label)
    {
        if (!ResolveSendMethod())
        {
            GUI.AddMessage("FreeHeal: ClientPeer.Send не найден", DangerColor);
            return;
        }
        if (Timing.TotalTime - _lastSendTime < SendCooldown)
        {
            GUI.AddMessage("FreeHeal: кулдаун, подожди секунду", WarningColor);
            return;
        }

        _sendMethod.Invoke(GameMain.Client.ClientPeer, new object[] { msg, DeliveryMethod.Reliable, true });
        _lastSendTime = Timing.TotalTime;
        DebugConsole.NewMessage("[FreeHeal] отправлено: " + label, Color.Cyan);
    }

    // ClientPeer internal — конкретную реализацию Send ищем рефлексией
    // (тот же подход, что в SellByIdModule.ResolveSendMethod).
    private static bool ResolveSendMethod()
    {
        if (_sendMethodResolved) { return _sendMethod != null; }
        _sendMethodResolved = true;

        try
        {
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { return false; }

            _sendMethod = peer.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(m =>
                {
                    var ps = m.GetParameters();
                    return m.Name == "Send" && ps.Length == 3 &&
                           ps[0].ParameterType != typeof(byte[]);
                });
        }
        catch (Exception e)
        {
            LuaCsLogger.LogError("[FreeHeal] ResolveSendMethod: " + e);
        }
        return _sendMethod != null;
    }

    // WriteOnlyMessage internal → Activator по имени (как в SellByIdModule)
    private static IWriteMessage CreateMsg()
    {
        var t = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
            .FirstOrDefault(x => x.Name == "WriteOnlyMessage");
        return (IWriteMessage)Activator.CreateInstance(t);
    }

    // ------------------------------ цвета GUI -------------------------------
    private static Color DangerColor => Color.OrangeRed;
    private static Color WarningColor => Color.Yellow;
    private static Color AccentColor => Color.Cyan;
}
