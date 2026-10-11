// ============================================================================
//  RespecModule.cs — Respec-Anywhere (волна 656), добавка к TalentOverspendModule
// ============================================================================
//
//  ЧТО ЭТО: мгновенная смена ВЕСЬ билд талантов посреди раунда.
//    FLOOD (все таланты) -> REFUND (снос дерева, очки в бюджет) -> FLOOD (новый набор)
//    XP-налог респека (450+500*уровень за каждый прошлый сброс) игнорируется,
//    т.к. новый набор заливается оверспендом.
//
//  РЕФАНД-ПАКЕТ (byte-в-byte ваниль, CharacterNetworking.cs:164-165):
//    ClientEventWrite для ConfirmRefundEventData пишет ТОЛЬКО
//    WriteRangedInteger((int)EventType.ConfirmTalentRefund, Min, Max) — тело пустое.
//    Серверный хендлер (CharacterNetworking.cs сервер:304):
//      case ConfirmTalentRefund: CanManageTalents(c) -> Info.RefundTalents();
//    RefundTalents: гейт один — TalentRefundPoints > 0; сносит UnlockedTalents,
//    SavedStatValues, ResetTalents(talentResetCount), сжигает refund point.
//
//  ГДЕ ВЗЯТЬ REFUND POINTS: талант с эффектом refundtalents=true
//    (Эндокрин-бустер и пр.) даёт +1 очко. Сам цикл выглядит так:
//    1) FLOOD одним пакетом талантов ВНЕ дерева? Нет — рефанд-очко даёт
//       только статаффект. Практичный порядок:
//       a) FLOOD полного дерева (в т.ч. талант с refundtalents, если он
//          есть в твоём дереве — есть у большинства профессий);
//       b) REFUND — дерево снеслось, заливаем новый набор FLOOD'ом.
//    Если refund-очков нет: пакет молча проигнорируется (гейт <=0) —
//    зальи дерево ещё раз и попробуй после следующего refund-таланта.
//
//  ЗАВИСИМОСТЬ: использует TalentOverspendEngine из TalentOverspendModule.cs
//  (Start/Stop/Update). Если тот файл не загружен — модуль сообщит об этом.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class RespecAnywhereModule : CSModuleBase
    {
        public override string Id   => "respec_anywhere";
        public override string Name => "Respec Anywhere";
        public override string Description =>
            "Instant mid-round talent respec: REFUND packet (ConfirmTalentRefund, empty body) wipes the " +
            "tree and refunds points; re-flood with Talent Overspend ignores the XP tax. " +
            "Needs >=1 refund point (talents with refundtalents effect, e.g. Endocrine Booster).";
        public override string Category => "exploit";

        public override string GetLabel() => "Respec Anywhere";

        public override void OnClick()
        {
            int refundPoints = 0;
            try { refundPoints = Character.Controlled?.Info?.TalentRefundPoints ?? 0; } catch { }

            var box = new GUIMessageBox(
                "RESPEC ANYWHERE",
                "Cycle: REFUND (wipes talents + refunds points) -> re-FLOOD (new build, free).\n" +
                "The XP tax of vanilla respecs is bypassed by the overspend flood.\n" +
                "\n" +
                "Your refund points: " + refundPoints + "\n" +
                "(+1 from talents with the refundtalents effect — Endocrine Booster etc.)\n" +
                "\n" +
                "Prepare: open Talent Overspend first if you want an automatic re-flood\n" +
                "with a DIFFERENT subset — or just re-run Flood manually after REFUND.",
                new LocalizedString[]
                {
                    "REFUND NOW",
                    "Auto: Flood+Refund+Flood",
                    "Close",
                },
                new Vector2(0.6f, 0.65f));

            box.Buttons[0].Color = Color.Orange;
            box.Buttons[1].Color = Color.LimeGreen;
            box.Buttons[2].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                try { RespecAnywhereEngine.SendRefund(); }
                catch (Exception e) { DebugConsole.ThrowError("[Respec] failed", e); }
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                try { RespecAnywhereEngine.AutoRespec(); }
                catch (Exception e) { DebugConsole.ThrowError("[Respec] failed", e); }
                return true;
            };

            box.Buttons[2].OnClicked = (_, _) => { box.Close(); return true; };
        }
    }

    internal static class RespecAnywhereEngine
    {
        private static Harmony _harmony;
        private static bool _patched;
        private static bool _refundQueued;

        private enum AutoPhase { Idle, WaitWipe, WaitFloodDone }
        private static AutoPhase _phase;
        private static double _phaseSince;
        private static int _preWipeCount;

        private const double WipeTimeout = 10.0;

        /// <summary>
        /// REFUND-пакет: Character.UpdateTalents-событие с ConfirmRefundEventData.
        /// Ванильный ClientEventWrite пишет для него только ranged-заголовок
        /// (тело пустое), поэтому НЕ перехватываем — шлём как есть.
        /// </summary>
        public static string SendRefund()
        {
            var c = Character.Controlled;
            if (c == null) { return "нет персонажа (нужно быть в раунде)"; }
            if (GameMain.Client == null) { return "только как клиент MP"; }
            if (c.Info == null) { return "нет CharacterInfo"; }

            int before = c.Info.TalentRefundPoints;
            if (before <= 0)
            {
                return "refund points = 0 — сервер молча проигнорирует (нужен талант с эффектом refundtalents)";
            }

            GameMain.Client.CreateEntityEvent(c, new Character.ConfirmRefundEventData());
            return "REFUND отправлен (" + before + " очк.). Дерево снесётся на сервере; " +
                   "дальше — Talent Overspend Flood для нового билда.";
        }

        /// <summary>
        /// Авто-цикл: FLOOD (заливает дерево, включая refund-талант) -> REFUND ->
        /// ждём снос -> FLOOD снова (чистое дерево, оверспенд льёт всё).
        /// </summary>
        public static string AutoRespec()
        {
            if (Character.Controlled?.Info == null) { return "нет персонажа"; }
            if (GameMain.Client == null) { return "только как клиент MP"; }

            if (!_patched)
            {
                _harmony = new Harmony("cshub.respecanywhere");
                _harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ClientEventWrite)),
                    prefix: new HarmonyMethod(typeof(RespecAnywhereEngine), nameof(RespecAnywhereEngine.ClientEventWritePrefix)));
                _patched = true;
            }

            // 1) поднимаем оверспенд (он сам зальёт всё дерево)
            if (!TalentOverspendEngine.Enabled)
            {
                try { TalentOverspendEngine.Start(); }
                catch (Exception e) { return "TalentOverspendEngine.Start failed: " + e.Message; }
            }

            _phase = AutoPhase.WaitFloodDone;
            _phaseSince = Timing.TotalTime;
            _preWipeCount = Character.Controlled.Info.UnlockedTalents.Count;
            DebugConsole.NewMessage("[Respec] phase 1/3: FLOOD дерева (ждём завершения)...", Color.Orange);
            return "AUTO-RESPEC запущен: Flood -> Refund -> Flood. Логи в консоли.";
        }

        /// <summary>Опциональный памп; при отсутствии — вызывается из префикса записи.</summary>
        public static void Update()
        {
            if (_phase == AutoPhase.Idle) { return; }
            var c = Character.Controlled;
            if (c?.Info == null) { _phase = AutoPhase.Idle; return; }

            switch (_phase)
            {
                case AutoPhase.WaitFloodDone:
                {
                    // оверспенд сам Stop'нется с "DONE"; ловим момент: план пуст или
                    // UnlockedTalents перестали расти и движок остановился
                    if (!TalentOverspendEngine.Enabled)
                    {
                        // flood завершён (DONE или стоп) -> шлём REFUND
                        _phase = AutoPhase.WaitWipe;
                        _phaseSince = Timing.TotalTime;
                        _preWipeCount = c.Info.UnlockedTalents.Count;
                        DebugConsole.NewMessage("[Respec] phase 2/3: FLOOD завершён, шлю REFUND...", Color.Orange);
                        SendRefund();
                    }
                    else if (Timing.TotalTime - _phaseSince > 120.0)
                    {
                        DebugConsole.NewMessage("[Respec] flood не завершился за 120с — отмена", Color.Red);
                        _phase = AutoPhase.Idle;
                    }
                    break;
                }

                case AutoPhase.WaitWipe:
                {
                    // сервер применил рефанд, если UnlockedTalents у ОВАЛ>{
                    int nowCount = c.Info.UnlockedTalents.Count;
                    if (nowCount < _preWipeCount)
                    {
                        DebugConsole.NewMessage("[Respec] phase 3/3: дерево снесено (" +
                            _preWipeCount + " -> " + nowCount + "), заливаю заново...", Color.LightGreen);
                        // заливаем С НУЛЯ: запускаем оверспенд заново
                        try { TalentOverspendEngine.Start(); } catch { }
                        _phase = AutoPhase.WaitFloodDone;
                        _phaseSince = Timing.TotalTime;
                        // теперь цикл завершится, когда flood снова закончится
                    }
                    else if (Timing.TotalTime - _phaseSince > WipeTimeout)
                    {
                        DebugConsole.NewMessage("[Respec] рефанд не применился за 10с (нет refund points?) — стоп", Color.Red);
                        _phase = AutoPhase.Idle;
                    }
                    break;
                }
            }
        }

        // Пас-через префикс: если у нас запрошен рефанд-пакет — пропускаем ваниль
        // (она сама корректно напишет пустое тело). Нужен только чтобы подсветить
        // момент отправки и дёрнуть Update() на кадре записи события.
        public static bool ClientEventWritePrefix(Character __instance, IWriteMessage msg, NetEntityEvent.IData extraData)
        {
            if (_refundQueued && extraData is Character.ConfirmRefundEventData)
            {
                _refundQueued = false;
                DebugConsole.NewMessage("[Respec] ConfirmRefundEventData ушёл в сеть", Color.Orange);
            }
            return true; // всегда пропускаем ванильную запись
        }
    }
}
// ============================================================================
//  ИНТЕГРАЦИЯ С CSHUB:
//   - модуль RespecAnywhereModule появится в меню сам (CSModuleBase);
//   - для авто-цикла нужен памп: если у хаба есть свой Update-насос, вызывай
//     RespecAnywhereEngine.Update(); иначе движок TalentOverspend сам качает
//     свой Update через Character.Update postfix — Respec ждёт его остановки,
//     так что явный памп не обязателен для базового цикла.
//  ЗАМЕТКА БЕЗОПАСНОСТИ: при SendRefund без refund-очков сервер молчит
//   (гейт в RefundTalents) — не крашит, не палится, просто no-op.
// ============================================================================
