// ============================================================================
//  PropertyChangerPlus.cs — in-game редактор свойств + ПРЕСЕТЫ ИМБ (волна 685)
//
//  Что можно менять: [InGameEditable] + [ConditionallyEditable] свойства Item
//  и его ItemComponent'ов. Сервер применяет через Item.ReadPropertyChange,
//  проверяя CanClientAccess (дистанция/панель) и Condition.
//
//  ★ НОВОЕ (685): кнопки-пресеты имб на базе проверенных векторов:
//   «♾ Батарея-вечность» — ChangeProperty {Capacity=1e9, Charge=1e9}:
//     PowerContainer.Capacity клампится ТОЛЬКО снизу (Max(value,1)), верха нет.
//     Батарея держит миллиард заряда, разрядка практически не двигает,
//     при полном заряде потребление подзарядки = 0. Видно всей команде.
//   «☠ Убить батарею» — Capacity=1 (нижний кламп): разряд за один тик.
//     (Только свои батареи — CanClientAccess у чужих панелей не пройдёт.)
//
//  ИСПРАВЛЕНИЯ ПРЕДЫДУЩЕЙ ВЕРСИИ (сохранены):
//   1) У текстовых полей кнопка «Применить» (Selected=true в фокусе ломал ввод).
//   2) Показываем и ConditionallyEditable — сервер сам решает (его условие).
//   3) ChangeProperty через CreateEntityEvent(requireControlledCharacter:true).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class PropertyChangerPlus : CSModuleBase
    {
        public override string Id => "property_changer_plus";
        public override string Name => "Property Changer+";

        private GUIMessageBox _menu;
        private GUIListBox _itemList;
        private GUIListBox _propList;
        private GUITextBlock _header;
        private GUITickBox _showAllTick;

        private Item _currentItem;

        public override string GetLabel() => "Property Changer+";

        public override void OnClick()
        {
            if (_menu != null)
            {
                _menu.Close();
                _menu = null;
                return;
            }
            OpenWindow();
        }

        private void OpenWindow()
        {
            _menu = new GUIMessageBox(
                headerText: "Property Changer+",
                text: "",
                buttons: Array.Empty<LocalizedString>(),
                relativeSize: new Vector2(0.82f, 0.84f));

            var root = _menu.Content;

            // ---- строка 1: заголовок + обновить + tick ----
            var topRow = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.06f), root.RectTransform),
                isHorizontal: true,
                childAnchor: Anchor.CenterLeft)
            { RelativeSpacing = 0.02f };

            _header = new GUITextBlock(
                new RectTransform(new Vector2(0.6f, 1f), topRow.RectTransform),
                "Предмет не выбран",
                textAlignment: Alignment.CenterLeft,
                textColor: Color.Cyan);

            var refreshBtn = new GUIButton(
                new RectTransform(new Vector2(0.16f, 1f), topRow.RectTransform),
                new RawLString("Обновить"));
            refreshBtn.OnClicked = (_, _) => { RefreshAll(); return true; };

            _showAllTick = new GUITickBox(
                new RectTransform(new Vector2(0.2f, 1f), topRow.RectTransform),
                new RawLString("Все editable"))
            { Selected = true };
            _showAllTick.OnSelected = _ => { BuildPropertyList(); return true; };

            // ---- строка 2: пресеты ----
            var presetRow = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.06f), root.RectTransform),
                isHorizontal: true,
                childAnchor: Anchor.CenterLeft)
            { RelativeSpacing = 0.02f };

            var presetLbl = new GUITextBlock(
                new RectTransform(new Vector2(0.24f, 1f), presetRow.RectTransform),
                "ПРЕСЕТЫ (прицельная батарея):",
                textAlignment: Alignment.CenterLeft,
                textColor: Color.Khaki);

            var infBtn = new GUIButton(
                new RectTransform(new Vector2(0.3f, 1f), presetRow.RectTransform),
                new RawLString("♾ Батарея-вечность"));
            infBtn.Color = Color.MediumSpringGreen;
            infBtn.ToolTip = "Capacity=1e9 + Charge=1e9 (кламп только снизу). Вечная энергия, видно всем.";
            infBtn.OnClicked = (_, _) => { ApplyInfinityBattery(); return true; };

            var killBtn = new GUIButton(
                new RectTransform(new Vector2(0.24f, 1f), presetRow.RectTransform),
                new RawLString("☠ Разрядить"));
            killBtn.Color = Color.IndianRed;
            killBtn.ToolTip = "Capacity=1 — разрядится за один тик";
            killBtn.OnClicked = (_, _) => { ApplyKillBattery(); return true; };

            var split = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.8f), root.RectTransform),
                isHorizontal: true,
                childAnchor: Anchor.TopLeft)
            { RelativeSpacing = 0.02f, Stretch = true };

            _itemList = new GUIListBox(new RectTransform(new Vector2(0.32f, 1f), split.RectTransform))
            {
                OnSelected = (comp, data) =>
                {
                    if (data is Item it) SelectItem(it);
                    return true;
                }
            };

            _propList = new GUIListBox(new RectTransform(new Vector2(0.66f, 1f), split.RectTransform));

            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.07f), root.RectTransform),
                "Ищи «PowerContainer.Capacity» / «.Charge» в списке свойств — правь руками и жми Применить.",
                wrap: true, textColor: Color.LightGray);

            RefreshAll();
        }

        // ================================================================
        //  ПРЕСЕТЫ (волна 685)
        // ================================================================
        private static PowerContainer GetTargetPowerContainer()
        {
            // приоритет: прицел > в руках > ближайший в 450
            var controlled = Character.Controlled;
            if (controlled == null) { return null; }

            PowerContainer FromItem(Item it)
                => it?.GetComponent<PowerContainer>();

            var focused = FromItem(controlled.FocusedItem);
            if (focused != null) { return focused; }

            foreach (var held in controlled.HeldItems)
            {
                var pc = FromItem(held);
                if (pc != null) { return pc; }
            }

            PowerContainer best = null;
            float bestD = 450f * 450f;
            foreach (var it in Item.ItemList)
            {
                if (it == null || it.Removed) { continue; }
                var pc = FromItem(it);
                if (pc == null) { continue; }
                float d = Vector2.DistanceSquared(it.WorldPosition, controlled.WorldPosition);
                if (d < bestD) { bestD = d; best = pc; }
            }
            return best;
        }

        private void ApplyInfinityBattery()
        {
            var pc = GetTargetPowerContainer();
            if (pc == null)
            {
                GUI.AddMessage("PropertyChanger+: батарея не найдена (наведи прицел на PowerContainer)",
                    Color.Orange);
                return;
            }

            // ChangeProperty требует SerializableProperty — берём reflection'ом
            var item = pc.Item;
            var capProp = FindProp(item, pc, "Capacity");
            var chargeProp = FindProp(item, pc, "Charge");
            if (capProp == null || chargeProp == null)
            {
                GUI.AddMessage("PropertyChanger+: Capacity/Charge property не найдены", Color.Red);
                return;
            }

            SendChange(item, capProp, pc, 1_000_000_000f);
            SendChange(item, chargeProp, pc, 1_000_000_000f);

            GUI.AddMessage("PropertyChanger+: ♾ батарея «" + item.Name + "» — Capacity/Charge = 1e9. Вечная энергия!",
                Color.MediumSpringGreen);
        }

        private void ApplyKillBattery()
        {
            var pc = GetTargetPowerContainer();
            if (pc == null)
            {
                GUI.AddMessage("PropertyChanger+: батарея не найдена", Color.Orange);
                return;
            }

            var item = pc.Item;
            var capProp = FindProp(item, pc, "Capacity");
            if (capProp == null) { GUI.AddMessage("Capacity property не найдена", Color.Red); return; }

            SendChange(item, capProp, pc, 1f);
            GUI.AddMessage("PropertyChanger+: ☠ батарея «" + item.Name + "» — Capacity=1, разрядится мгновенно",
                Color.IndianRed);
        }

        private static SerializableProperty FindProp(Item item, ItemComponent comp, string name)
        {
            foreach (var p in SerializableProperty.GetProperties<InGameEditable>(comp))
            {
                if (p.Name == name) { return p; }
            }
            foreach (var p in SerializableProperty.GetProperties<ConditionallyEditable>(comp))
            {
                if (p.Name == name) { return p; }
            }
            // fallback: свойства на самом Item
            foreach (var p in SerializableProperty.GetProperties<InGameEditable>(item))
            {
                if (p.Name == name) { return p; }
            }
            return null;
        }

        private static void SendChange(Item item, SerializableProperty prop, object parent, object value)
        {
            try { prop.TrySetValue(parent, value); }
            catch (Exception e) { DebugConsole.ThrowError("PropertyChangerPlus local set failed", e); }

            try
            {
                GameMain.Client.CreateEntityEvent(
                    item,
                    new Item.ChangePropertyEventData(prop, parent as ISerializableEntity),
                    requireControlledCharacter: true);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("PropertyChangerPlus network send failed", e);
            }
        }

        // ================================================================
        //  ДАЛЬШЕ — стандартный редактор (как было)
        // ================================================================
        private void RefreshAll()
        {
            RefreshItemList();
            BuildPropertyList();
        }

        private void SelectItem(Item item)
        {
            _currentItem = item;
            if (_header != null)
            {
                _header.Text = item == null
                    ? "Предмет не выбран"
                    : $"{item.Name}  |  ID {item.ID}";
            }
            BuildPropertyList();
        }

        private IEnumerable<Item> GetNearbyItems(float range = 450f)
        {
            var controlled = Character.Controlled;
            if (controlled == null) return Enumerable.Empty<Item>();

            Vector2 origin = controlled.WorldPosition;
            return Item.ItemList
                .Where(it => it != null && !it.Removed)
                .Select(it => (it, d: Vector2.DistanceSquared(it.WorldPosition, origin)))
                .Where(x => x.d < range * range)
                .OrderBy(x => x.d)
                .Select(x => x.it);
        }

        private void RefreshItemList()
        {
            if (_itemList == null) return;
            _itemList.ClearChildren();

            var controlled = Character.Controlled;
            var focused = controlled?.FocusedItem;
            var selected = controlled?.SelectedItem;
            var secondary = controlled?.SelectedSecondaryItem;

            var added = new HashSet<ushort>();
            void AddItem(Item it, string tag, Color color)
            {
                if (it == null || it.Removed || !added.Add(it.ID)) return;

                var frame = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.065f), _itemList.Content.RectTransform))
                { UserData = it };

                new GUITextBlock(
                    new RectTransform(new Vector2(1f, 1f), frame.RectTransform),
                    tag.Length == 0 ? it.Name : $"[{tag}] {it.Name}",
                    textColor: color);
            }

            AddItem(focused, "прицел", Color.Yellow);
            AddItem(selected, "в руках", Color.Lime);
            AddItem(secondary, "secondary", Color.LightGreen);
            foreach (var it in GetNearbyItems()) AddItem(it, "рядом", Color.White);

            if (_currentItem == null || _currentItem.Removed)
            {
                if (_itemList.Content.CountChildren > 0)
                    _itemList.Select(0);
            }
            else
            {
                for (int i = 0; i < _itemList.Content.CountChildren; i++)
                {
                    var child = _itemList.Content.GetChild(i);
                    if (child.UserData is Item it && it == _currentItem)
                    {
                        _itemList.Select(i, force: GUIListBox.Force.Yes,
                            playSelectSound: GUIListBox.PlaySelectSound.No);
                        break;
                    }
                }
            }
        }

        private readonly struct PropEntry
        {
            public readonly object Parent;
            public readonly SerializableProperty Property;
            public readonly string Label;
            public readonly bool Conditional;

            public PropEntry(object parent, SerializableProperty property, string label, bool conditional)
            {
                Parent = parent;
                Property = property;
                Label = label;
                Conditional = conditional;
            }
        }

        private List<PropEntry> EnumerateProperties(Item item)
        {
            var result = new List<PropEntry>();
            if (item == null) return result;

            bool showAll = _showAllTick?.Selected ?? true;

            foreach (var p in SerializableProperty.GetProperties<InGameEditable>(item))
                result.Add(new PropEntry(item, p, "Item." + p.Name, false));

            if (showAll)
            {
                foreach (var p in SerializableProperty.GetProperties<ConditionallyEditable>(item))
                    result.Add(new PropEntry(item, p, "Item." + p.Name, true));
            }

            foreach (var comp in item.Components)
            {
                if (comp == null) continue;
                bool componentEditable = comp.AllowInGameEditing;

                foreach (var p in SerializableProperty.GetProperties<InGameEditable>(comp))
                {
                    if (componentEditable)
                        result.Add(new PropEntry(comp, p, comp.Name + "." + p.Name, false));
                }

                if (showAll && componentEditable)
                {
                    foreach (var p in SerializableProperty.GetProperties<ConditionallyEditable>(comp))
                        result.Add(new PropEntry(comp, p, comp.Name + "." + p.Name, true));
                }
            }

            return result;
        }

        private void BuildPropertyList()
        {
            if (_propList == null) return;
            _propList.ClearChildren();

            if (_currentItem == null || _currentItem.Removed)
            {
                new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.08f), _propList.Content.RectTransform),
                    "Нет предмета.", textColor: Color.Gray);
                return;
            }

            var props = EnumerateProperties(_currentItem);
            if (props.Count == 0)
            {
                new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.08f), _propList.Content.RectTransform),
                    "У этого предмета нет in-game editable свойств.", textColor: Color.Gray);
                return;
            }

            foreach (var entry in props)
                AddPropertyRow(entry);
        }

        private void AddPropertyRow(PropEntry entry)
        {
            var row = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.13f), _propList.Content.RectTransform),
                isHorizontal: true, childAnchor: Anchor.CenterLeft)
            { RelativeSpacing = 0.015f };

            var lbl = new GUITextBlock(
                new RectTransform(new Vector2(0.36f, 1f), row.RectTransform),
                entry.Label + (entry.Conditional ? "  [cond]" : ""),
                wrap: true,
                textColor: entry.Conditional ? Color.Khaki : Color.White);
            lbl.CanBeFocused = false;

            Type type = entry.Property.PropertyType;
            object currentValue = entry.Property.GetValue(entry.Parent);

            GUIComponent editor = BuildEditor(row, entry, type, currentValue);
            if (editor == null)
            {
                new GUITextBlock(
                    new RectTransform(new Vector2(0.40f, 1f), row.RectTransform),
                    $"<{type.Name}> не поддерживается", textColor: Color.Gray);
            }
        }

        private GUIComponent BuildEditor(GUILayoutGroup parent, PropEntry entry, Type type, object currentValue)
        {
            if (type == typeof(bool))
            {
                var tick = new GUITickBox(
                    new RectTransform(new Vector2(0.40f, 1f), parent.RectTransform),
                    new RawLString(Convert.ToString(currentValue is true)))
                { Selected = currentValue is true };
                tick.OnSelected = tb =>
                {
                    tick.Text = tb.Selected ? "true" : "false";
                    SendChange(_currentItem, entry.Property, entry.Parent, tb.Selected);
                    return true;
                };
                return tick;
            }

            if (type.IsEnum)
            {
                var drop = new GUIDropDown(
                    new RectTransform(new Vector2(0.40f, 1f), parent.RectTransform));
                foreach (var name in Enum.GetNames(type))
                    drop.AddItem(new RawLString(name), Enum.Parse(type, name));
                if (currentValue != null) drop.SelectItem((int)currentValue);
                drop.OnSelected = (_, data) =>
                {
                    SendChange(_currentItem, entry.Property, entry.Parent, data);
                    return true;
                };
                return drop;
            }

            if (type == typeof(string) || type == typeof(Identifier))
            {
                var box = new GUITextBox(
                    new RectTransform(new Vector2(0.4f, 1f), parent.RectTransform),
                    currentValue?.ToString() ?? "");
                MakeApplyButton(parent, () =>
                {
                    string s = box.Text;
                    int max = entry.Property.GetAttribute<Editable>()?.MaxLength ?? 0;
                    if (max > 0 && s.Length > max) s = s.Substring(0, max);
                    object v = type == typeof(Identifier) ? (object)s.ToIdentifier() : s;
                    SendChange(_currentItem, entry.Property, entry.Parent, v);
                });
                return box;
            }

            if (type == typeof(int))
            {
                var box = new GUITextBox(
                    new RectTransform(new Vector2(0.32f, 1f), parent.RectTransform),
                    currentValue?.ToString() ?? "0");
                MakeApplyButton(parent, () =>
                {
                    if (int.TryParse(box.Text, out int v))
                        SendChange(_currentItem, entry.Property, entry.Parent, v);
                });
                return box;
            }

            if (type == typeof(float))
            {
                var box = new GUITextBox(
                    new RectTransform(new Vector2(0.32f, 1f), parent.RectTransform),
                    (currentValue is float f ? f : 0f).ToString("G", CultureInfo.InvariantCulture));
                MakeApplyButton(parent, () =>
                {
                    if (float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                        SendChange(_currentItem, entry.Property, entry.Parent, v);
                });
                return box;
            }

            if (type == typeof(Color))
            {
                var c = currentValue is Color cc ? cc : Color.White;
                var group = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.28f, 1f), parent.RectTransform), isHorizontal: true);
                var r = MakeSmallBox(group, c.R);
                var g = MakeSmallBox(group, c.G);
                var b = MakeSmallBox(group, c.B);
                var a = MakeSmallBox(group, c.A);
                MakeApplyButton(parent, () =>
                {
                    if (byte.TryParse(r.Text, out byte rv) && byte.TryParse(g.Text, out byte gv) &&
                        byte.TryParse(b.Text, out byte bv) && byte.TryParse(a.Text, out byte av))
                        SendChange(_currentItem, entry.Property, entry.Parent, new Color(rv, gv, bv, av));
                });
                return group;
            }

            if (type == typeof(Vector2) || type == typeof(Vector3) || type == typeof(Vector4))
            {
                int n = type == typeof(Vector2) ? 2 : type == typeof(Vector3) ? 3 : 4;
                var boxes = new GUITextBox[n];
                var group = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.28f, 1f), parent.RectTransform), isHorizontal: true);
                for (int i = 0; i < n; i++)
                {
                    float val = 0f;
                    if (currentValue is Vector2 v2 && i < 2) val = i == 0 ? v2.X : v2.Y;
                    else if (currentValue is Vector3 v3 && i < 3) val = i == 0 ? v3.X : i == 1 ? v3.Y : v3.Z;
                    else if (currentValue is Vector4 v4) val = i == 0 ? v4.X : i == 1 ? v4.Y : i == 2 ? v4.Z : v4.W;
                    boxes[i] = MakeSmallBox(group, val);
                }
                MakeApplyButton(parent, () =>
                {
                    var vals = new float[n];
                    for (int i = 0; i < n; i++)
                        if (!float.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i]))
                            return;
                    object v = n == 2 ? (object)new Vector2(vals[0], vals[1])
                             : n == 3 ? (object)new Vector3(vals[0], vals[1], vals[2])
                             : (object)new Vector4(vals[0], vals[1], vals[2], vals[3]);
                    SendChange(_currentItem, entry.Property, entry.Parent, v);
                });
                return group;
            }

            if (type == typeof(Point))
            {
                var p = currentValue is Point pp ? pp : Point.Zero;
                var group = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.28f, 1f), parent.RectTransform), isHorizontal: true);
                var x = MakeSmallBox(group, p.X);
                var y = MakeSmallBox(group, p.Y);
                MakeApplyButton(parent, () =>
                {
                    if (int.TryParse(x.Text, out int xv) && int.TryParse(y.Text, out int yv))
                        SendChange(_currentItem, entry.Property, entry.Parent, new Point(xv, yv));
                });
                return group;
            }

            if (type == typeof(Rectangle))
            {
                var r = currentValue is Rectangle rr ? rr : Rectangle.Empty;
                var group = new GUILayoutGroup(
                    new RectTransform(new Vector2(0.28f, 1f), parent.RectTransform), isHorizontal: true);
                var x = MakeSmallBox(group, r.X);
                var y = MakeSmallBox(group, r.Y);
                var w = MakeSmallBox(group, r.Width);
                var h = MakeSmallBox(group, r.Height);
                MakeApplyButton(parent, () =>
                {
                    if (int.TryParse(x.Text, out int xv) && int.TryParse(y.Text, out int yv) &&
                        int.TryParse(w.Text, out int wv) && int.TryParse(h.Text, out int hv))
                        SendChange(_currentItem, entry.Property, entry.Parent, new Rectangle(xv, yv, wv, hv));
                });
                return group;
            }

            return null;
        }

        private static GUIButton MakeApplyButton(GUILayoutGroup parent, Action onClick)
        {
            var btn = new GUIButton(
                new RectTransform(new Vector2(0.13f, 1f), parent.RectTransform),
                new RawLString("Применить"))
            { Color = Color.DarkGreen };
            btn.OnClicked = (_, _) =>
            {
                try { onClick(); }
                catch (Exception e) { DebugConsole.ThrowError("PropertyChangerPlus apply failed", e); }
                return true;
            };
            return btn;
        }

        private static GUITextBox MakeSmallBox(GUILayoutGroup parent, float value) =>
            new GUITextBox(
                new RectTransform(new Vector2(0.23f, 1f), parent.RectTransform),
                value.ToString("G", CultureInfo.InvariantCulture));

        private static GUITextBox MakeSmallBox(GUILayoutGroup parent, int value) =>
            new GUITextBox(
                new RectTransform(new Vector2(0.23f, 1f), parent.RectTransform),
                value.ToString(CultureInfo.InvariantCulture));
    }
}
