// ============================================================================
//  PacketDumpModule.cs — CSHUB drop-in (v8: junk filter + strings + dedup)
//  Captures incoming and outgoing network packets, SKIPPING known noise,
//  DECODES readable identifiers from the bit stream, SUPPRESSES duplicates.
//
//  V8 (по дампу #3): junk 32B keepalive = IN 02 UPDATE_LOBBY len<64
//  ("02 B9 00 00 ..."). Сервер шлёт его пачками — скипаем. Крупные 02
//  (>=64B) оставляем: там магазины/фракции/сторы кампании в открытом виде.
//  + дедуп: подряд идущие идентичные пакеты (длина+первые 32B совпали)
//  скипаются со счётчиком — сервер часто дублирует sync-бёрсты.
//
//  V7 STRING HARVESTER: идентификаторы (предметы/магазины/фракции/имена)
//  пишутся length-prefixed ASCII на СМЕЩЁННОЙ битовой позиции; сдвиг буфера
//  вправо на 0..7 бит делает их читаемыми. Проверено на живых дампах:
//  s1:merchantclowns s1:wateringcan s1:huskcult s1:revolverround и т.д.
//
//  V6 JUNK FILTER: SKIP OUT 07/06, OUT 01 <MinStateBytes, IN 0C/0D/0A/0B,
//  IN 03 <MinStateBytes. KEEP: IN 1A MONEY, OUT 0A/12/13 (деньги!), IN 03
//  >=MinStateBytes (спавн-бёрсты), IN 15/16 (события/предатели!), остальное.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ==========================================================================
    //  GUI entry — CSHUB menu button
    // ==========================================================================
    public class PacketDumpModule : CSModuleBase
    {
        public override string Id   => "packet_dump";
        public override string Name => "Packet Dump";
        public override string Description =>
            "Захват сетевых пакетов (без мусора).\n\n" +
            "• Скипает пинги/keepalive/флад состояния\n" +
            "• Логирует транзакции: MONEY, SERVER_COMMAND,\n" +
            "  крупные спавн-бёрсты (>=800B)\n" +
            "• Пишет в cshub_packetdump.txt рядом с игрой";
        public override string Category => "debug";

        public override string GetLabel() => "Packet Dump";

        public override void OnClick()
        {
            string status = PacketDumpEngine.Enabled
                ? $"RUNNING — {PacketDumpEngine.PacketsCaptured} captured, {PacketDumpEngine.SkippedTotal} skipped"
                : "idle";

            var box = new GUIMessageBox(
                "PACKET DUMP",
                $"Captures valuable packets, skips junk.\nStatus: {status}\nLog: cshub_packetdump.txt",
                new LocalizedString[]
                {
                    PacketDumpEngine.Enabled ? "Stop" : "Start",
                    PacketDumpEngine.FilterJunk ? "Фильтр: ВКЛ" : "Фильтр: ВЫКЛ",
                    "Close",
                },
                new Vector2(0.55f, 0.5f));

            box.Buttons[0].Color = PacketDumpEngine.Enabled ? Color.OrangeRed : Color.LimeGreen;
            box.Buttons[1].Color = PacketDumpEngine.FilterJunk ? Color.LimeGreen : Color.DarkGray;
            box.Buttons[2].Color = Color.DarkGray;

            box.Buttons[0].OnClicked = (_, _) =>
            {
                try
                {
                    if (PacketDumpEngine.Enabled)
                        PacketDumpEngine.Stop();
                    else
                        PacketDumpEngine.Start();
                    box.Buttons[0].Text = PacketDumpEngine.Enabled ? "Stop" : "Start";
                    box.Buttons[0].Color = PacketDumpEngine.Enabled ? Color.OrangeRed : Color.LimeGreen;
                }
                catch (Exception e)
                {
                    DebugConsole.ThrowError("[PacketDump] toggle failed", e);
                }
                return true;
            };

            box.Buttons[1].OnClicked = (_, _) =>
            {
                PacketDumpEngine.FilterJunk = !PacketDumpEngine.FilterJunk;
                box.Buttons[1].Text = PacketDumpEngine.FilterJunk ? "Фильтр: ВКЛ" : "Фильтр: ВЫКЛ";
                box.Buttons[1].Color = PacketDumpEngine.FilterJunk ? Color.LimeGreen : Color.DarkGray;
                DebugConsole.NewMessage(
                    "[PacketDump] фильтр мусора: " + (PacketDumpEngine.FilterJunk ? "ВКЛ" : "ВЫКЛ"),
                    PacketDumpEngine.FilterJunk ? Color.LimeGreen : Color.Orange);
                return true;
            };

            box.Buttons[2].OnClicked = (_, _) => { box.Close(); return true; };
        }
    }

    // ==========================================================================
    //  Engine
    // ==========================================================================
    internal static class PacketDumpEngine
    {
        public static bool Enabled { get; private set; }
        public static int PacketsCaptured { get; private set; }
        public static int SkippedTotal => _skippedPing + _skippedState;

        /// <summary>Фильтр мусора. Переключается кнопкой в GUI.</summary>
        public static bool FilterJunk = true;

        /// <summary>
        /// Порог для UPDATE_INGAMode-флада: пакеты МЕНЬШЕ этого размера (байт)
        /// считаются спамом состояния и скипаются. 800 = граница между
        /// "спам состояния" (399-706B в дампе) и "спавн-бёрст покупок" (897+B).
        /// </summary>
        public static int MinStateBytes = 800;

        private static StreamWriter _logFile;
        private static string _logPath;

        private static Harmony _harmony;
        private static bool _patched;

        private const int MaxLogPerFrame = 15;
        private static int _logThisFrame;
        private static float _lastFrameTime;

        private static int _skippedPing;
        private static int _skippedState;
        private static int _skippedDup;

        // дедуп: len + первые 32 байта предыдущего залогированного пакета
        private static int _prevLen = -1;
        private static readonly byte[] _prevHead = new byte[32];

        public static void Start()
        {
            if (Enabled) { return; }

            if (!_patched)
            {
                _harmony = new Harmony("cshub.packetdump");
                int patched = 0;

                // OUT: ClientPeer.Send is ABSTRACT — can't patch abstract methods.
                // Instead, get the CONCRETE implementation from the live instance.
                var client = GameMain.Client;
                if (client?.ClientPeer != null)
                {
                    var peerType = client.ClientPeer.GetType();
                    var sendMethod = AccessTools.Method(peerType, "Send",
                        new[] { typeof(IWriteMessage), typeof(DeliveryMethod), typeof(bool) });
                    if (sendMethod != null && !sendMethod.IsAbstract)
                    {
                        _harmony.Patch(sendMethod,
                            prefix: new HarmonyMethod(typeof(PacketDumpEngine), nameof(SendPrefix)));
                        patched++;
                        DebugConsole.NewMessage($"[PacketDump] patched {peerType.Name}.Send (OUT)", Color.Gray);
                    }
                    else
                    {
                        DebugConsole.NewMessage($"[PacketDump] {peerType.Name}.Send not found or abstract!", Color.Red);
                    }
                }
                else
                {
                    DebugConsole.NewMessage("[PacketDump] Client or ClientPeer is null!", Color.Red);
                }

                // IN: GameClient.ReadDataMessage(IReadMessage) — the first dispatch point
                // for ALL incoming application-level packets.
                var readMethod = AccessTools.Method(typeof(GameClient), "ReadDataMessage");
                if (readMethod != null)
                {
                    _harmony.Patch(readMethod,
                        prefix: new HarmonyMethod(typeof(PacketDumpEngine), nameof(ReadPrefix)));
                    patched++;
                    DebugConsole.NewMessage("[PacketDump] patched ReadDataMessage (IN)", Color.Gray);
                }
                else
                {
                    DebugConsole.NewMessage("[PacketDump] ReadDataMessage not found!", Color.Red);
                }

                if (patched == 0)
                {
                    DebugConsole.NewMessage("[PacketDump] nothing could be patched!", Color.Red);
                    return;
                }
                _patched = true;
                DebugConsole.NewMessage($"[PacketDump] {patched} hooks installed", Color.Gray);
            }

            Enabled = true;
            PacketsCaptured = 0;
            _skippedPing = 0;
            _skippedState = 0;
            _skippedDup = 0;
            _prevLen = -1;
            try
            {
                _logPath = Path.Combine("cshub_packetdump.txt");
                _logFile = new StreamWriter(_logPath, append: false) { AutoFlush = false };
                _logFile.WriteLine($"// Packet Dump started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _logFile.WriteLine("// Format: [timestamp] DIRECTION length_B [extra] | HEX [| decoded strings]");
                _logFile.WriteLine("// v8: junk filter + string harvester + duplicate suppression");
                _logFile.WriteLine("//   skip: OUT 07/06, OUT 01 <" + MinStateBytes + "B, IN 0C/0D/0A/0B, IN 02 <64B, IN 03 <" + MinStateBytes + "B, dups");
                _logFile.WriteLine("//   keep: IN 1A MONEY, OUT 0A/12/13, IN 15/16 (события), IN 03 >=" + MinStateBytes + "B, всё остальное");
                _logFile.Flush();
                DebugConsole.NewMessage($"[PacketDump] logging to {_logPath} (junk filter: {(FilterJunk ? "ON" : "OFF")})", Color.LimeGreen);
            }
            catch (Exception e)
            {
                DebugConsole.NewMessage($"[PacketDump] file logging unavailable: {e.Message}", Color.Orange);
                _logFile = null;
            }
            DebugConsole.NewMessage("[PacketDump] started — capturing valuable packets", Color.LimeGreen);
        }

        public static void Stop()
        {
            if (!Enabled) { return; }
            Enabled = false;
            try
            {
                _logFile?.WriteLine($"// stopped: {PacketsCaptured} captured, {_skippedPing} pings skipped, {_skippedState} state packets skipped, {_skippedDup} duplicates skipped");
                _logFile?.Flush();
                _logFile?.Dispose();
            }
            catch { }
            _logFile = null;
            DebugConsole.NewMessage($"[PacketDump] stopped — {PacketsCaptured} captured, {_skippedPing}+{_skippedState}+{_skippedDup} junk skipped, log: {_logPath}", Color.Orange);
        }

        // ---------------------------------------------------------- OUT prefix
        // ClientPeer.Send(IWriteMessage msg, DeliveryMethod deliveryMethod, bool compress)
        private static void SendPrefix(IWriteMessage msg, DeliveryMethod deliveryMethod, bool compressPastThreshold)
        {
            if (!Enabled) { return; }
            DumpPacket(msg?.Buffer, msg?.LengthBytes ?? 0, "→ OUT", deliveryMethod.ToString(), Color.Yellow, incoming: false);
        }

        // ---------------------------------------------------------- IN prefix
        // GameClient.ReadDataMessage(IReadMessage inc)
        private static void ReadPrefix(IReadMessage inc)
        {
            if (!Enabled) { return; }
            DumpPacket(inc?.Buffer, inc?.LengthBytes ?? 0, "← IN", "", Color.Cyan, incoming: true);
        }

        // ---------------------------------------------------------- junk filter
        // Все правила — по первому байту (header) + направлению. Сверены с
        // живым дампом пользователя (волны 224-225) и enum-ами заголовков.
        private static bool IsJunk(byte header, int len, bool incoming)
        {
            if (incoming)
            {
                // ServerPacketHeader
                switch (header)
                {
                    case (byte)ServerPacketHeader.PING_REQUEST:          // 0C — эхо-пинг 66B каждые ~2с
                    case (byte)ServerPacketHeader.CLIENT_PINGS:          // 0D — keepalive 5B
                    case (byte)ServerPacketHeader.VOICE:                 // 0A — войс
                    case (byte)ServerPacketHeader.VOICE_AMPLITUDE_DEBUG: // 0B — войс-отладка
                        _skippedPing++;
                        return true;

                    case (byte)ServerPacketHeader.UPDATE_INGAME:         // 03 — флад состояния сущностей
                        if (len < MinStateBytes) { _skippedState++; return true; }
                        return false; // крупные = спавн-бёрсты покупок, логируем

                    case (byte)ServerPacketHeader.UPDATE_LOBBY:          // 02 — 32B keepalive лобби ("02 B9 ...")
                        if (len < 64) { _skippedState++; return true; }
                        return false; // крупные лобби-синки = магазины/сторы кампании, оставляем

                    default:
                        return false;
                }
            }
            else
            {
                // ClientPacketHeader
                switch (header)
                {
                    case (byte)ClientPacketHeader.PING_RESPONSE:         // 07 — ответ на пинг 66B
                    case (byte)ClientPacketHeader.VOICE:                 // 06 — войс
                        _skippedPing++;
                        return true;

                    case (byte)ClientPacketHeader.UPDATE_INGAME:         // 01 — инпут персонажа
                        if (len < MinStateBytes) { _skippedState++; return true; }
                        return false;

                    default:
                        return false; // 0A SERVER_COMMAND и прочее — ценное
                }
            }
        }

        // ---------------------------------------------------------- shared

        private static void DumpPacket(byte[] data, int len, string dir, string extra, Color color, bool incoming)
        {
            if (data == null || len < 2) { return; }

            if (FilterJunk && IsJunk(data[0], len, incoming))
            {
                // раз в 500 скипов пишем строку-маячок в файл, чтобы видеть,
                // что фильтр жив, не раздувая лог
                if ((_skippedPing + _skippedState) % 500 == 0)
                {
                    try
                    {
                        _logFile?.WriteLine($"// ...skipped {_skippedPing} pings + {_skippedState} state packets");
                        _logFile?.Flush();
                    }
                    catch { }
                }
                return;
            }

            // дедуп: идентичный (len + первые 32B) подряд идущий пакет — скип.
            // Сервер часто дублирует sync-бёрсты; в дампе это мусор.
            bool isDup = len == _prevLen;
            if (isDup)
            {
                int headLen = Math.Min(len, 32);
                for (int i = 0; i < headLen; i++)
                {
                    if (data[i] != _prevHead[i]) { isDup = false; break; }
                }
            }
            if (isDup)
            {
                _skippedDup++;
                if (_skippedDup % 500 == 0)
                {
                    try { _logFile?.WriteLine("// ...skipped " + _skippedDup + " duplicate packets"); _logFile?.Flush(); } catch { }
                }
                return;
            }
            _prevLen = len;
            for (int i = 0; i < Math.Min(len, 32); i++) { _prevHead[i] = data[i]; }

            PacketsCaptured++;
            if (!ShouldLog()) { return; }

            int dumpLen = Math.Min(len, 256);
            string hex = HexDump(data, dumpLen); // cap at 256B per line
            string extraStr = string.IsNullOrEmpty(extra) ? "" : $" [{extra}]";
            string strings = HarvestStrings(data, dumpLen);
            string line = $"[{Timing.TotalTime:F3}] {dir} {len}B{extraStr} | {hex}{strings}";

            // console (rate-limited)
            DebugConsole.NewMessage(line, color);

            // file (no rate limit — everything goes to file)
            try
            {
                if (_logFile != null)
                {
                    _logFile.WriteLine(line);
                    if (PacketsCaptured % 50 == 0) { _logFile.Flush(); }
                }
            }
            catch { }
        }

        private static bool ShouldLog()
        {
            float now = (float)Timing.TotalTime;
            if (now != _lastFrameTime)
            {
                _lastFrameTime = now;
                _logThisFrame = 0;
            }
            _logThisFrame++;
            return _logThisFrame <= MaxLogPerFrame;
        }

        private static string HexDump(byte[] data, int count)
        {
            var sb = new StringBuilder(count * 3);
            for (int i = 0; i < count && i < data.Length; i++)
            {
                sb.Append(data[i].ToString("X2"));
                if (i < count - 1) { sb.Append(' '); }
            }
            if (data.Length > count) { sb.Append("..."); }
            return sb.ToString();
        }

        // ---------------------------------------------------------- v7: string harvester
        // Идентификаторы в потоке — length-prefixed ASCII на смещённой битовой
        // позиции. Сдвигаем буфер вправо на 0..7 бит и ищем ASCII-раны >= 5.
        // Возвращает "" или " | s1:merchantclowns s1:wateringcan ..." (до 8 штук).
        private static string HarvestStrings(byte[] data, int count)
        {
            var found = new List<string>();
            var seen = new HashSet<string>();
            var run = new List<byte>();

            for (int shift = 0; shift < 8; shift++)
            {
                run.Clear();
                for (int i = 0; i <= count; i++)
                {
                    byte b;
                    if (i < count)
                    {
                        if (shift == 0)
                        {
                            b = data[i];
                        }
                        else
                        {
                            int next = (i + 1 < count) ? data[i + 1] : 0;
                            b = (byte)((data[i] >> shift) | (next << (8 - shift)));
                        }
                    }
                    else
                    {
                        b = 0; // терминатор — закрыть ран
                    }

                    if (b >= 0x20 && b < 0x7F)
                    {
                        run.Add(b);
                        if (run.Count > 64) { run.RemoveAt(0); } // длинные раны — не идентификаторы
                    }
                    else
                    {
                        if (run.Count >= 5)
                        {
                            string s = Encoding.ASCII.GetString(run.ToArray());
                            if (seen.Add(s) && found.Count < 8)
                            {
                                found.Add("s" + shift + ":" + s);
                            }
                        }
                        run.Clear();
                    }
                }
            }

            return found.Count == 0 ? "" : " | " + string.Join(" ", found);
        }
    }
}
// ============================================================================
//  Passive diagnostic. No packet modification.
//  Both patch targets are Barotrauma's own classes (not Lidgren), so Harmony
//  can access them from a mod assembly without cross-assembly issues.
//  IWriteMessage/IReadMessage expose public Buffer (byte[]) and LengthBytes (int)
//  so no reflection on internal fields is needed either.
//  v6: junk rules by header byte + direction; skip counters; GUI toggle.
// ============================================================================
