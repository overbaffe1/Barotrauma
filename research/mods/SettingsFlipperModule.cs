using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  SETTINGS FLIPPER (волна 525) — пульт по каналу SERVER_SETTINGS.
    //
    //  ГЕЙТ: ServerSettings.ServerRead (server ServerSettings.cs:167):
    //      if (!c.HasPermission(ManageSettings)) return;
    //  Единственная проверка — прямой перм ManageSettings (0x200) или
    //  роль хоста (у хоста листен-сервера все пермы). Фоллбэка «пермлесс =
    //  все» тут НЕТ. Всё, что делает модуль, пишется в серверный лог.
    //
    //  ЧТО ВНУТРИ (всё проверено по коду):
    //  • Настройки сервера + KarmaManager синкаются по одному каналу:
    //    netProperties — словарь [MD5-hash(имя свойства, lowercase) →
    //    SerializableProperty]. Клиент присылает ключ-хеш и значение,
    //    сервер делает property.SetValue НАПРЯМУЮ (NetPropertyData.Read).
    //    float/int — РЕЖЕТСЯ НИЧЕМ: NaN в VoteRequiredRatio валиден.
    //  • bool — идёт строкой ("True"/"False") через TrySetValue(string).
    //  • Ключ хеша: MD5(UTF8(name.ToLower)) → rotate-left-5 xor байтов
    //    (ToolBoxCore.StringToUInt32Hash, код публичен), key==0 → 1.
    //  • EXTRA CARGO: ReadExtraCargo НЕ проверяет AllowAsExtraCargo на
    //    сервере (только что это ItemPrefab) → любой предмет до 10 шт
    //    × 20 типов, спавнится в суб на старте следующего раунда
    //    (GameServer:3167, если supplies ещё не спавнились).
    //
    //  ПАКЕТ (зеркало ServerRead при flags=Properties):
    //   byte SERVER_SETTINGS(2) | byte flags=0x04
    //   u32 extraCargoCount [+ {Identifier, byte}×]
    //   u32 propCount [+ {u32 key, payload}×]
    //   bool monstersChanged=false + pad
    //   VariableUInt32 banRemoveCount=0
    //  payload: bool → WriteString("True"/"False"); float/int →
    //   VariableUInt32(4) + 4 байта. Битфилдов нет — чистые байты.
    // ============================================================
    public class SettingsFlipperModule : CSModuleBase
    {
        public override string Id   => "settings_flipper";
        public override string Name => "Settings Flipper";
        public override string Description =>
            "Пульт настроек сервера через SERVER_SETTINGS.\n\n" +
            "• Требует перм ManageSettings (хост листен-сервера = есть)\n" +
            "• Дистанционные интеракции: продажа/апгрейды отовсюду\n" +
            "• Карма OFF, мгновенная доставка покупок, анти-кик голосования\n" +
            "• Retaliation 99999с (лицензия на ответку), ExtraCargo спавнер\n" +
            "• Кастом: любое [Serialize]-свойство (bool/float/int)\n" +
            "Все изменения видны в серверном логе.";
        public override string Category => "exploit";

        private static GUIMessageBox _box;
        private static GUITextBox _customName;
        private static GUITextBox _customValue;
        private static GUITextBox _cargoId;
        private static GUITextBox _cargoAmount;
        private static GUITextBlock _status;

        public override string GetLabel() => "Settings Flipper 🎛";

        public override void OnClick()
        {
            if (_box != null) { _box.Close(); _box = null; return; }

            if (GameMain.Client?.ClientPeer == null)
            {
                GUI.AddMessage("[Flipper] Только в мультиплеере", Color.Orange);
                return;
            }

            _box = new GUIMessageBox(
                headerText: "SETTINGS FLIPPER",
                text: "",
                buttons: new LocalizedString[] { },
                relativeSize: new Vector2(0.68f, 0.8f));

            var content = _box.Content;
            content.ClearChildren();

            bool hasPerm = GameMain.Client.HasPermission(ClientPermissions.ManageSettings);
            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.09f), content.RectTransform),
                (hasPerm ? "перм ManageSettings: ЕСТЬ — сервер применит" :
                           "перм ManageSettings: НЕТ — сервер проигнорирует (шлём всё равно)"),
                textAlignment: Alignment.CenterLeft, wrap: true, font: GUIStyle.SmallFont);
            _status.TextColor = hasPerm ? new Color(120, 255, 140) : new Color(255, 180, 80);

            // --- пресеты: одно свойство на кнопку ---
            var presets = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.52f), content.RectTransform),
                isHorizontal: false);
            presets.RelativeSpacing = 0.006f;

            AddPreset(presets, "Remote interactions ON (продажа/апгрейды отовсюду)",
                "AllowRemoteCampaignInteractions", PropValue.Bool(true));
            AddPreset(presets, "Immediate delivery ON (покупки сразу в инвентарь)",
                "AllowImmediateItemDelivery", PropValue.Bool(true));
            AddPreset(presets, "Karma OFF (без штрафов кармы)",
                "KarmaEnabled", PropValue.Bool(false));
            AddPreset(presets, "Retaliation license 99999с (ответка без кармы)",
                "AllowedRetaliationTime", PropValue.Float(99999f));
            AddPreset(presets, "ANTI-VOTE: VoteRequiredRatio = NaN (никакое голосование не пройдёт)",
                "VoteRequiredRatio", PropValue.Float(float.NaN));
            AddPreset(presets, "Kick votes 100% (кик только единогласно)",
                "KickVoteRequiredRatio", PropValue.Float(1f));
            AddPreset(presets, "Transfer request cap 99 999 999",
                "MaximumMoneyTransferRequest", PropValue.Int(99999999));

            // --- кастом ---
            var customRow = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.09f), content.RectTransform),
                isHorizontal: true);
            customRow.RelativeSpacing = 0.008f;
            _customName = new GUITextBox(
                new RectTransform(new Vector2(0.45f, 1f), customRow.RectTransform),
                "ИмяСвойства");
            _customValue = new GUITextBox(
                new RectTransform(new Vector2(0.33f, 1f), customRow.RectTransform),
                "True");
            var customBtn = new GUIButton(
                new RectTransform(new Vector2(0.2f, 1f), customRow.RectTransform), "SET");
            customBtn.Color = new Color(90, 70, 140);
            customBtn.OnClicked = (b, d) =>
            {
                string name = (_customName.Text ?? "").Trim();
                string raw = (_customValue.Text ?? "").Trim();
                if (name.Length == 0) { Msg("пустое имя", Color.Red); return true; }

                if (raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    raw.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    SendProperty(name, PropValue.Bool(raw.Equals("true", StringComparison.OrdinalIgnoreCase)));
                }
                else if (float.TryParse(raw.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float f))
                {
                    SendProperty(name, PropValue.Float(f));
                }
                else
                {
                    SendProperty(name, PropValue.Str(raw));
                }
                Msg("SET " + name + " = " + raw, Color.Cyan);
                return true;
            };

            // --- extra cargo ---
            var cargoRow = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.09f), content.RectTransform),
                isHorizontal: true);
            cargoRow.RelativeSpacing = 0.008f;
            _cargoId = new GUITextBox(
                new RectTransform(new Vector2(0.55f, 1f), cargoRow.RectTransform),
                "identifier предмета");
            _cargoAmount = new GUITextBox(
                new RectTransform(new Vector2(0.18f, 1f), cargoRow.RectTransform),
                "10");
            var cargoBtn = new GUIButton(
                new RectTransform(new Vector2(0.25f, 1f), cargoRow.RectTransform), "+CARGO");
            cargoBtn.Color = new Color(40, 110, 70);
            cargoBtn.ToolTip = "Спавн в суб на старте следующего раунда (до 10/тип, 20 типов)";
            cargoBtn.OnClicked = (b, d) =>
            {
                string id = (_cargoId.Text ?? "").Trim();
                if (!int.TryParse((_cargoAmount.Text ?? "").Trim(), out int n)) { n = 1; }
                n = Math.Clamp(n, 1, 10);
                if (id.Length == 0) { Msg("пустой identifier", Color.Red); return true; }
                SendExtraCargo(id, n);
                Msg("cargo: " + id + " x" + n + " (спавн на след. раунде)", Color.Lime);
                return true;
            };
        }

        private static void Msg(string text, Color color) => GUI.AddMessage("[Flipper] " + text, color);

        private static void AddPreset(GUILayoutGroup parent, string label, string propName, PropValue value)
        {
            var btn = new GUIButton(
                new RectTransform(new Vector2(1f, 0.135f), parent.RectTransform), label);
            btn.Color = new Color(45, 60, 85);
            btn.ToolTip = propName;
            btn.OnClicked = (b, d) =>
            {
                try
                {
                    SendProperty(propName, value);
                    Msg(propName + " отправлено", Color.Lime);
                }
                catch (Exception e)
                {
                    Msg("фейл: " + e.Message, Color.Red);
                }
                return true;
            };
        }

        // ---- значения свойств ----
        private readonly struct PropValue
        {
            public enum Kind { Bool, Float, Int, Str }
            public Kind K { get; }
            public bool B { get; }
            public float F { get; }
            public int I { get; }
            public string S { get; }

            private PropValue(Kind k) { K = k; B = default; F = default; I = default; S = null; }
            public static PropValue Bool(bool b) => new PropValue(Kind.Bool) { B = b };
            public static PropValue Float(float f) => new PropValue(Kind.Float) { F = f };
            public static PropValue Int(int i) => new PropValue(Kind.Int) { I = i };
            public static PropValue Str(string s) => new PropValue(Kind.Str) { S = s };
        }

        // ---- хеш ключа (точная копия ToolBoxCore.StringToUInt32Hash) ----
        private static readonly MD5 _md5 = MD5.Create();

        private static uint PropertyKey(string name)
        {
            byte[] input = Encoding.UTF8.GetBytes(name.ToLowerInvariant());
            byte[] hash = _md5.ComputeHash(input);
            uint key = 0;
            foreach (byte b in hash)
            {
                key = (key << 5) | (key >> 27);
                key ^= b;
            }
            return key == 0 ? 1u : key;
        }

        // ---- отправка: пакет с одним свойством ----
        private static void SendProperty(string name, PropValue value)
        {
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { throw new InvalidOperationException("нет соединения"); }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS);
            msg.WriteByte(0x04);              // NetFlags.Properties

            msg.WriteUInt32(0);               // extraCargo: пусто

            msg.WriteUInt32(1);               // properties: 1 шт
            msg.WriteUInt32(PropertyKey(name));
            switch (value.K)
            {
                case PropValue.Kind.Bool:
                    // bool не поддержан в Read — идёт строкой через default:
                    // [len][bytes], сервер TrySetValue("True"/"False")
                    msg.WriteString(value.B ? "True" : "False");
                    break;
                case PropValue.Kind.Float:
                    msg.WriteVariableUInt32(4);
                    msg.WriteSingle(value.F); // NaN/∞ проходят — клампов нет
                    break;
                case PropValue.Kind.Int:
                    msg.WriteVariableUInt32(4);
                    msg.WriteInt32(value.I);
                    break;
                default:
                    msg.WriteString(value.S ?? "");
                    break;
            }

            msg.WriteBoolean(false);          // monstersChanged
            msg.WritePadBits();
            msg.WriteVariableUInt32(0);       // banlist removals: 0

            peer.Send(msg, DeliveryMethod.Reliable);
        }

        // ---- отправка: ExtraCargo (только cargo-секция) ----
        private static void SendExtraCargo(string prefabIdentifier, int amount)
        {
            var peer = GameMain.Client?.ClientPeer;
            if (peer == null) { throw new InvalidOperationException("нет соединения"); }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS);
            msg.WriteByte(0x04);

            msg.WriteUInt32(1);               // extraCargo: 1 позиция
            msg.WriteIdentifier(prefabIdentifier.ToIdentifier());
            msg.WriteByte((byte)amount);

            msg.WriteUInt32(0);               // properties: пусто

            msg.WriteBoolean(false);
            msg.WritePadBits();
            msg.WriteVariableUInt32(0);

            peer.Send(msg, DeliveryMethod.Reliable);
        }

        public override void Update() { }

        public override void Dispose()
        {
            _box?.Close();
            _box = null;
            base.Dispose();
        }
    }
}
