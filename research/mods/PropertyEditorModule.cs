using System;
using System.Collections.Generic;
using System.Globalization;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  МОДУЛЬ 1: редактор InGameEditable-свойств любого предмета.
    //  Наводишься на предмет (или подводишь курсор) → меню со ВСЕМИ
    //  in-game редактируемыми свойствами (автодетект, зеркалит список
    //  сервера GetInGameEditableProperties) → меняешь значение →
    //  клиент шлёт легитный ChangeProperty-ивент. Сервер (волна 448)
    //  НЕ клампит float/Vector2 и т.д. — NaN/1e30 применяются как есть.
    //  Гейт сервера: CanClientAccess → надо стоять рядом с предметом.
    // ============================================================
    public class ItemPropertyEditorModule : CSModuleBase
    {
        public override string Id   => "item_property_editor";
        public override string Name => "Item Property Editor";
        public override string Description =>
            "Редактор свойств предметов через ChangeProperty (эксплойт 448).\n\n" +
            "• Автодетект предмета: что подсвечено прицелом (FocusedItem)\n" +
            "  или ближайший к курсору\n" +
            "• Полный список InGameEditable-свойств (как на сервере)\n" +
            "• Быстрые кнопки NaN / MAX / 0 для чисел\n" +
            "• Сервер применяет без клампов — NaN проходит насквозь\n" +
            "• Нужен доступ к предмету (CanClientAccess — быть рядом)";
        public override string Category => "exploit";

        private static GUIMessageBox _menu;
        private static GUIListBox _list;
        private static GUITextBlock _titleLabel;

        public override string GetLabel() => "Property Editor 🛠";

        public override void OnClick()
        {
            if (_menu != null) { CloseMenu(); return; }

            Item target = FindTarget();
            if (target == null)
            {
                GUI.AddMessage("[PropEdit] Наведись на предмет (подсветка прицела) или подведи курсор ближе", Color.Orange);
                return;
            }

            OpenMenu(target);
        }

        // ===== ПОИСК ЦЕЛИ =====
        private static Item FindTarget()
        {
            // 1) то, что игра сама подсвечивает при наведении
            Item focused = Character.Controlled?.FocusedItem;
            if (focused != null && !focused.Removed) return focused;

            // 2) фоллбэк: ближайший к курсору предмет (порог = дистанция + половина размера)
            Item item = ItemPropertyNet.FindUnderCursor(120f);
            return item;
        }

        // ===== МЕНЮ =====
        private static void OpenMenu(Item item)
        {
            CloseMenu();

            List<ItemPropertyNet.EditablePropRef> props = ItemPropertyNet.Collect(item);

            _menu = new GUIMessageBox(
                "PROPERTY EDITOR",
                "",
                Array.Empty<LocalizedString>(),
                new Vector2(0.72f, 0.85f));

            var content = _menu.Content;
            content.ClearChildren();

            // --- header ---
            var header = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.09f), content.RectTransform, Anchor.TopCenter),
                style: null);
            header.Color = new Color(30, 40, 55, 255);

            var headerLayout = new GUILayoutGroup(
                new RectTransform(new Vector2(0.97f, 0.9f), header.RectTransform, Anchor.Center),
                isHorizontal: true);
            headerLayout.RelativeSpacing = 0.01f;

            _titleLabel = new GUITextBlock(
                new RectTransform(new Vector2(0.64f, 1f), headerLayout.RectTransform),
                item.Name + "  #" + item.ID + "\n" +
                "свойств: " + props.Count + " | сервер применит только если можешь взаимодействовать (рядом)",
                textAlignment: Alignment.CenterLeft);
            _titleLabel.TextColor = new Color(100, 220, 255);
            _titleLabel.Font = GUIStyle.Font;

            var refreshBtn = new GUIButton(
                new RectTransform(new Vector2(0.17f, 0.8f), headerLayout.RectTransform),
                "Обновить");
            refreshBtn.Color = new Color(60, 120, 80);
            refreshBtn.OnClicked = (b, d) => { OpenMenu(item); return true; };

            var closeBtn = new GUIButton(
                new RectTransform(new Vector2(0.17f, 0.8f), headerLayout.RectTransform),
                "Закрыть");
            closeBtn.Color = new Color(120, 60, 60);
            closeBtn.OnClicked = (b, d) => { CloseMenu(); return true; };

            // --- список ---
            var listFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.9f), content.RectTransform, Anchor.BottomCenter),
                style: null);
            listFrame.Color = new Color(18, 24, 36, 230);

            _list = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
            _list.Color = new Color(18, 24, 36, 230);

            if (props.Count == 0)
            {
                var empty = new GUITextBlock(
                    new RectTransform(new Vector2(0.96f, 0.06f), _list.Content.RectTransform),
                    "У предмета нет in-game редактируемых свойств (сервер их не примет).",
                    textAlignment: Alignment.CenterLeft);
                empty.TextColor = new Color(220, 160, 90);
                return;
            }

            foreach (var pref in props)
                ItemPropertyNet.RenderRow(_list, pref);

            GUI.AddMessage("[PropEdit] " + item.Name + ": найдено свойств " + props.Count, Color.Cyan);
        }

        private static void CloseMenu()
        {
            _menu?.Close();
            _menu = null;
            _list = null;
            _titleLabel = null;
        }

        public override void Update() { }

        public override void Dispose()
        {
            CloseMenu();
            base.Dispose();
        }
    }

    // ============================================================
    //  МОДУЛЬ 2: насос → NaN в set_speed (эксплойт 448 + Divide NaN).
    //  Прямого клиентского пути «шлём сигнал» нет — сигналы живут
    //  только в проводах. Зато любой ИСТОЧНИК на проводе насоса
    //  (memory/oscillator/delay/...) имеет редактируемые свойства:
    //  пишем в них NaN → "NaN" уходит по проводу в set_speed →
    //  TryParse(NumberStyles.Any) ест "NaN" → насос отравлен навсегда
    //  (сеттер FlowPercentage проверяет СТАРОЕ значение и умирает).
    // ============================================================
    public class PumpPoisonModule : CSModuleBase
    {
        public override string Id   => "pump_poison";
        public override string Name => "Pump NaN Poison";
        public override string Description =>
            "Наведись на насос → модуль найдёт все предметы, подключённые\n" +
            "к нему проводами (memory, oscillator, delay, wifi, ...) и\n" +
            "позволит записать NaN в их редактируемые свойства.\n" +
            "NaN по проводу уходит в set_speed/set_targetlevel насоса —\n" +
            "насос и энергосеть отравлены до конца раунда.\n" +
            "Нет подключённых источников? Подключи memory к set_speed проводом.";
        public override string Category => "exploit";

        private static GUIMessageBox _menu;

        public override string GetLabel() => "Pump Poison ☠";

        public override void OnClick()
        {
            if (_menu != null) { _menu.Close(); _menu = null; return; }

            Item focus = Character.Controlled?.FocusedItem;
            Item pump = focus != null && !focus.Removed ? focus : ItemPropertyNet.FindUnderCursor(200f);

            if (pump == null || pump.GetComponent<Barotrauma.Items.Components.Pump>() == null)
            {
                GUI.AddMessage("[PumpPoison] Наведись на НАСОС (подсветка прицела)", Color.Orange);
                return;
            }

            OpenMenu(pump);
        }

        private static void OpenMenu(Item pump)
        {
            _menu?.Close();

            // собираем предметы на другом конце проводов насоса
            var wiredItems = new List<Item>();
            int wireCount = 0;
            if (pump.Connections != null)
            {
                foreach (Connection conn in pump.Connections)
                {
                    if (conn == null) continue;
                    foreach (Wire w in conn.Wires)
                    {
                        // Wire — ItemComponent, у него нет Removed; живость смотрим у предмета
                        if (w == null || w.Item == null || w.Item.Removed) continue;
                        wireCount++;
                        Item a = w.Connections[0]?.Item;
                        Item b = w.Connections[1]?.Item;
                        Item other = ReferenceEquals(a, pump) ? b : ReferenceEquals(b, pump) ? a : null;
                        if (other == null || ReferenceEquals(other, pump)) continue;
                        if (!wiredItems.Contains(other)) wiredItems.Add(other);
                    }
                }
            }

            var props = new List<ItemPropertyNet.EditablePropRef>();
            foreach (Item wired in wiredItems)
                props.AddRange(ItemPropertyNet.Collect(wired));

            _menu = new GUIMessageBox(
                "PUMP POISON",
                "",
                Array.Empty<LocalizedString>(),
                new Vector2(0.72f, 0.85f));

            var content = _menu.Content;
            content.ClearChildren();

            var header = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.11f), content.RectTransform, Anchor.TopCenter),
                style: null);
            header.Color = new Color(45, 30, 30, 255);

            string headerText = pump.Name + " #" + pump.ID + "\n" +
                "проводов: " + wireCount + " | источников: " + wiredItems.Count +
                " | редактируемых свойств: " + props.Count;

            var title = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.9f), header.RectTransform, Anchor.Center),
                headerText, textAlignment: Alignment.CenterLeft);
            title.TextColor = new Color(255, 170, 120);
            title.Font = GUIStyle.Font;

            var listFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.88f), content.RectTransform, Anchor.BottomCenter),
                style: null);
            listFrame.Color = new Color(24, 18, 18, 230);

            var list = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
            list.Color = new Color(24, 18, 18, 230);

            if (props.Count == 0)
            {
                var hint = new GUITextBlock(
                    new RectTransform(new Vector2(0.96f, 0.12f), list.Content.RectTransform),
                    "На проводах насоса нет in-game редактируемых свойств.\n" +
                    "Схема: поставь memory component, провод memory signal_out → насос set_speed,\n" +
                    "потом снова нажми модуль — и отравишь NaN-строкой.\n" +
                    "Альтернатива: oscillator (Frequency=NaN) или divide 0/0 в цепи set_speed.",
                    textAlignment: Alignment.CenterLeft);
                hint.TextColor = new Color(220, 160, 90);
                hint.Font = GUIStyle.Font;
                return;
            }

            foreach (var pref in props)
                ItemPropertyNet.RenderRow(list, pref);

            GUI.AddMessage("[PumpPoison] " + pump.Name + ": целей " + props.Count, Color.Orange);
        }

        public override void Update() { }

        public override void Dispose()
        {
            _menu?.Close();
            _menu = null;
            base.Dispose();
        }
    }

    // ============================================================
    //  ОБЩАЯ МЕХАНИКА: сбор InGameEditable-свойств + отправка
    //  ChangeProperty ровно как ванильный клиент.
    // ============================================================
    internal static class ItemPropertyNet
    {
        public struct EditablePropRef
        {
            public Item Item;
            public ISerializableEntity Entity;   // Item или ItemComponent
            public SerializableProperty Property;
            public string OwnerLabel;
        }

        // Зеркало серверного GetInGameEditableProperties(ignoreConditions: true):
        // InGameEditable + ВСЕ ConditionallyEditable; у компонентов ещё
        // требуется AllowInGameEditing. Иначе сервер свойство не найдёт.
        public static List<EditablePropRef> Collect(Item item)
        {
            var result = new List<EditablePropRef>();
            if (item == null || item.Removed) return result;

            AddFrom(item, item, "Item", result);

            foreach (ItemComponent ic in item.GetComponents<ItemComponent>())
            {
                if (ic == null || !ic.AllowInGameEditing) continue;
                AddFrom(item, ic, ic.GetType().Name, result);
            }
            return result;
        }

        private static void AddFrom(Item item, ISerializableEntity entity, string label, List<EditablePropRef> result)
        {
            if (entity.SerializableProperties == null) return;
            foreach (SerializableProperty p in entity.SerializableProperties.Values)
            {
                if (p == null) continue;
                // InGameEditable / ConditionallyEditable — internal классы, из мода
                // не достать напрямую. Зеркалим серверный список по имени атрибута.
                bool inGame = false;
                foreach (Attribute a in p.Attributes)
                {
                    if (a == null) continue;
                    string an = a.GetType().Name;
                    if (an == "InGameEditable" || an == "ConditionallyEditable") { inGame = true; break; }
                }
                if (!inGame) continue;
                result.Add(new EditablePropRef
                {
                    Item = item,
                    Entity = entity,
                    Property = p,
                    OwnerLabel = label
                });
            }
        }

        // Отправка: 1) пишем значение локально (пакет сериализует ТЕКУЩЕЕ
        // значение свойства!), 2) создаём ChangeProperty-ивент.
        public static bool Send(EditablePropRef pref, object value)
        {
            try
            {
                if (pref.Item == null || pref.Item.Removed) return false;
                if (!pref.Property.TrySetValue(pref.Entity, value))
                {
                    GUI.AddMessage("[PropEdit] TrySetValue не смог записать значение", Color.Red);
                    return false;
                }
                if (GameMain.Client == null)
                {
                    // сингл: применяем локально, шлюз не нужен
                    return true;
                }
                GameMain.NetworkMember.CreateEntityEvent(
                    pref.Item, new Item.ChangePropertyEventData(pref.Property, pref.Entity));
                return true;
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[PropEdit] send error: " + e.Message);
                return false;
            }
        }

        // ===== СТРОКА ТАБЛИЦЫ =====
        public static void RenderRow(GUIListBox list, EditablePropRef pref)
        {
            SerializableProperty p = pref.Property;
            Type t = p.PropertyType;
            bool editableType =
                t == typeof(float) || t == typeof(int) || t == typeof(bool) ||
                t == typeof(string) || t == typeof(Identifier) || t == typeof(Vector2);

            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.075f), list.Content.RectTransform),
                style: null);
            row.Color = new Color(28, 36, 50, 190);

            var layout = new GUILayoutGroup(
                new RectTransform(new Vector2(0.98f, 0.92f), row.RectTransform, Anchor.Center),
                isHorizontal: true);
            layout.RelativeSpacing = 0.01f;

            // имя
            object cur = null;
            try { cur = p.GetValue(pref.Entity); } catch { }
            string nameStr = pref.OwnerLabel + "." + p.Name + " [" + ShortType(t) + "]";
            var name = new GUITextBlock(
                new RectTransform(new Vector2(0.33f, 1f), layout.RectTransform),
                nameStr + (editableType ? "" : "  (только чтение)"),
                textAlignment: Alignment.CenterLeft);
            name.TextColor = editableType ? new Color(210, 215, 225) : new Color(120, 120, 140);
            name.Font = GUIStyle.SmallFont;

            // текущее
            var curText = new GUITextBlock(
                new RectTransform(new Vector2(0.13f, 1f), layout.RectTransform),
                Format(cur),
                textAlignment: Alignment.Center);
            curText.TextColor = new Color(160, 180, 210);
            curText.Font = GUIStyle.SmallFont;

            if (!editableType) return;

            GUITextBox input = null;
            GUITextBox inputY = null;

            if (t == typeof(bool))
            {
                bool val = cur is bool b && b;
                var tick = new GUITickBox(
                    new RectTransform(new Vector2(0.2f, 0.9f), layout.RectTransform),
                    val ? "true" : "false");
                tick.Selected = val;
                tick.OnSelected = (tb) =>
                {
                    tick.Text = tb.Selected ? "true" : "false";
                    Apply(pref, tb.Selected);
                    return true;
                };
                return;
            }
            else if (t == typeof(Vector2))
            {
                Vector2 v = cur is Vector2 vv ? vv : Vector2.Zero;
                input = new GUITextBox(
                    new RectTransform(new Vector2(0.17f, 1f), layout.RectTransform),
                    v.X.ToString("0.###", CultureInfo.InvariantCulture));
                var comma = new GUITextBlock(
                    new RectTransform(new Vector2(0.03f, 1f), layout.RectTransform),
                    ";", textAlignment: Alignment.Center);
                inputY = new GUITextBox(
                    new RectTransform(new Vector2(0.17f, 1f), layout.RectTransform),
                    v.Y.ToString("0.###", CultureInfo.InvariantCulture));

                var okBtn = new GUIButton(
                    new RectTransform(new Vector2(0.12f, 1f), layout.RectTransform), "OK");
                okBtn.Color = new Color(60, 160, 60);
                okBtn.OnClicked = (btn, data) =>
                {
                    float x = ParseFloat(input.Text);
                    float y = ParseFloat(inputY.Text);
                    Apply(pref, new Vector2(x, y));
                    return true;
                };
                var nanBtn = new GUIButton(
                    new RectTransform(new Vector2(0.12f, 1f), layout.RectTransform), "NaN;NaN");
                nanBtn.Color = new Color(160, 60, 60);
                nanBtn.OnClicked = (btn, data) =>
                {
                    Apply(pref, new Vector2(float.NaN, float.NaN));
                    return true;
                };
                return;
            }
            else
            {
                input = new GUITextBox(
                    new RectTransform(new Vector2(0.3f, 1f), layout.RectTransform),
                    InputText(cur, t));
                input.TextColor = new Color(255, 220, 100);
            }

            // OK — применить введённое
            var ok = new GUIButton(
                new RectTransform(new Vector2(0.09f, 1f), layout.RectTransform), "OK");
            ok.Color = new Color(60, 160, 60);
            ok.OnClicked = (b, d) =>
            {
                object val;
                if (!TryParseValue(input.Text, t, out val))
                {
                    GUI.AddMessage("[PropEdit] Неверное значение для " + p.Name, Color.Red);
                    return true;
                }
                Apply(pref, val);
                return true;
            };

            // быстрые кнопки для чисел
            if (t == typeof(float))
            {
                var nanBtn = new GUIButton(
                    new RectTransform(new Vector2(0.09f, 1f), layout.RectTransform), "NaN");
                nanBtn.Color = new Color(160, 60, 60);
                nanBtn.OnClicked = (b, d) => { Apply(pref, float.NaN); return true; };

                var maxBtn = new GUIButton(
                    new RectTransform(new Vector2(0.1f, 1f), layout.RectTransform), "MAX");
                maxBtn.Color = new Color(170, 120, 40);
                maxBtn.OnClicked = (b, d) => { Apply(pref, float.MaxValue); return true; };

                var zeroBtn = new GUIButton(
                    new RectTransform(new Vector2(0.08f, 1f), layout.RectTransform), "0");
                zeroBtn.Color = new Color(70, 90, 130);
                zeroBtn.OnClicked = (b, d) => { Apply(pref, 0f); return true; };
            }
            else if (t == typeof(int))
            {
                var maxBtn = new GUIButton(
                    new RectTransform(new Vector2(0.1f, 1f), layout.RectTransform), "MAX");
                maxBtn.Color = new Color(170, 120, 40);
                maxBtn.OnClicked = (b, d) => { Apply(pref, int.MaxValue); return true; };

                var zeroBtn = new GUIButton(
                    new RectTransform(new Vector2(0.08f, 1f), layout.RectTransform), "0");
                zeroBtn.Color = new Color(70, 90, 130);
                zeroBtn.OnClicked = (b, d) => { Apply(pref, 0); return true; };
            }
            else if (t == typeof(string) || t == typeof(Identifier))
            {
                // подсказка: "NaN" как строку вводят руками в бокс
                var tip = new GUITextBlock(
                    new RectTransform(new Vector2(0.2f, 1f), layout.RectTransform),
                    "введи NaN руками",
                    textAlignment: Alignment.CenterLeft);
                tip.TextColor = new Color(140, 140, 160);
                tip.Font = GUIStyle.SmallFont;
            }
        }

        private static void Apply(EditablePropRef pref, object value)
        {
            if (Send(pref, value))
            {
                GUI.AddMessage("[PropEdit] " + pref.OwnerLabel + "." + pref.Property.Name +
                    " = " + Format(value) + " → отправлено", Color.Lime);
            }
        }

        // ===== УТИЛИТЫ =====
        public static Item FindUnderCursor(float maxDist)
        {
            Camera cam = (Screen.Selected as GameScreen)?.Cam;
            if (cam == null) return null;
            Vector2 mouseWorld = cam.ScreenToWorld(PlayerInput.MousePosition);
            float best = maxDist * maxDist;
            Item bestItem = null;
            foreach (Item item in Item.ItemList)
            {
                if (item == null || item.Removed || item.ParentInventory != null) continue;
                float pad = Math.Max(item.WorldRect.Width, item.WorldRect.Height) * 0.5f + 20f;
                float d = Vector2.DistanceSquared(mouseWorld, item.WorldPosition);
                if (d < (maxDist + pad) * (maxDist + pad) && d < best) { best = d; bestItem = item; }
            }
            return bestItem;
        }

        private static float ParseFloat(string s)
        {
            float.TryParse((s ?? "").Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out float v);
            return v;
        }

        private static bool TryParseValue(string raw, Type t, out object value)
        {
            value = null;
            string s = (raw ?? "").Trim();
            switch (Type.GetTypeCode(t == typeof(Identifier) ? typeof(string) : t))
            {
                case TypeCode.Single:
                    if (!float.TryParse(s.Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float f)) return false;
                    value = f; return true;
                case TypeCode.Int32:
                    if (!int.TryParse(s, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int i)) return false;
                    value = i; return true;
                case TypeCode.String:
                    value = t == typeof(Identifier) ? (object)s.ToIdentifier() : s;
                    return true;
                default:
                    return false;
            }
        }

        private static string InputText(object cur, Type t)
        {
            if (t == typeof(Identifier)) return cur is Identifier id ? id.Value : "";
            return cur is string s ? s : "";
        }

        private static string ShortType(Type t)
        {
            if (t == typeof(float)) return "f32";
            if (t == typeof(int)) return "int";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(string)) return "str";
            if (t == typeof(Identifier)) return "id";
            if (t == typeof(Vector2)) return "vec2";
            if (t == typeof(Color)) return "color";
            if (t == typeof(Vector3)) return "vec3";
            if (t == typeof(Vector4)) return "vec4";
            if (t == typeof(Point)) return "point";
            if (t == typeof(Rectangle)) return "rect";
            return t.Name;
        }

        private static string Format(object v)
        {
            if (v == null) return "null";
            if (v is float f) return f.ToString("0.###", CultureInfo.InvariantCulture);
            if (v is Identifier id) return id.Value;
            if (v is Vector2 vec) return ((int)vec.X) + ";" + ((int)vec.Y);
            if (v is bool b) return b ? "true" : "false";
            return v.ToString();
        }
    }
}
