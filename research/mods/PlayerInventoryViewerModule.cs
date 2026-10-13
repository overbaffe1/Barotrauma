// ============================================================================
//  PlayerInventoryViewerModule.cs — «Рентген экипажа» (волна 691)
//
//  Меню-просмотрщик: все персонажи сервера (игроки/боты/монстры, живые и
//  мёртвые) + содержимое их инвентарей С ИКОНКАМИ. Без вселения — чистый
//  клиентский просмотр: сервер сам синхронит инвентари персонажей.
//
//  ВКЛАДКИ: ИГРОКИ | БОТЫ | МОНСТРЫ. Поиск по имени. Раскрытие строки =
//  инвентарь: иконка, имя, стек, кондиция, ID. Авторефреш раз в 2с.
//
//  ЗАЧЕМ: PvP-разведка (кто тащит клубень/оружие), мониторинг админа,
//  поиск лута на монстрах.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class PlayerInventoryViewerModule : CSModuleBase
    {
        public override string Id   => "inv_viewer";
        public override string Name => "Inventory Viewer";
        public override string Description =>
            "Рентген экипажа: игроки/боты/монстры + инвентари с иконками. Клиентский просмотр.";
        public override string Category => "exploit";

        public override string GetLabel() => "Inv Viewer";

        private static GUIMessageBox _window;
        private static GUIListBox _list;
        private static GUITextBox _searchBox;
        private static GUIButton[] _tabs = new GUIButton[3];
        private static int _tab;                // 0 игроки / 1 боты / 2 монстры
        private static Character _expanded;
        private static string _search = "";
        private static int _uiSession;

        private static readonly Color TabOn  = new Color(90, 140, 200);
        private static readonly Color TabOff = new Color(50, 60, 80);
        private static readonly Color OkCol  = new Color(120, 255, 140);
        private static readonly Color BadCol = new Color(255, 110, 110);

        public override void OnClick()
        {
            try
            {
                if (_window != null) { CloseWindow(); return; }
                OpenWindow();
            }
            catch (Exception e) { LuaCsLogger.LogError("[InvViewer] " + e.Message); }
        }

        public override void Dispose() { CloseWindow(); base.Dispose(); }

        private static bool IsMonster(Character c)
        {
            try { return !c.IsHuman && !c.IsBot; } catch { return false; }
        }

        private static IEnumerable<Character> Filtered()
        {
            foreach (var c in Character.CharacterList)
            {
                if (c == null || c.Removed) { continue; }
                bool ok = _tab == 0 ? c.IsPlayer
                        : _tab == 1 ? (c.IsBot && c.IsHuman)
                        : IsMonster(c);
                if (!ok) { continue; }
                if (!string.IsNullOrEmpty(_search))
                {
                    string n = "";
                    try { n = c.DisplayName ?? c.Name ?? ""; } catch { }
                    if (n.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) { continue; }
                }
                yield return c;
            }
        }

        private static string StateOf(Character c)
        {
            try
            {
                if (c.IsDead) { return "мёртв"; }
                if (c.IsIncapacitated) { return "нок"; }
                if (c.Stun > 0.1f) { return "стан"; }
                return "жив";
            }
            catch { return "?"; }
        }

        private static string HpBar(Character c)
        {
            try
            {
                float max = c.MaxVitality <= 0 ? 100 : c.MaxVitality;
                float pct = Math.Clamp(c.Vitality / max, 0f, 1f);
                int b = (int)Math.Round(pct * 10);
                return "[" + new string('I', b) + new string('.', 10 - b) + "] " + (int)c.Vitality;
            }
            catch { return "[?]"; }
        }

        private static void OpenWindow()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("Inv Viewer: только клиент MP", BadCol);
                return;
            }

            _window = new GUIMessageBox("ИНВЕНТАРИ СЕРВЕРА", "",
                Array.Empty<LocalizedString>(), new Vector2(0.72f, 0.86f));
            var content = _window.Content;
            content.ClearChildren();

            var top = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.07f), content.RectTransform, Anchor.TopCenter),
                isHorizontal: true)
            { RelativeSpacing = 0.01f };

            string[] names = { "ИГРОКИ", "БОТЫ", "МОНСТРЫ" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                _tabs[i] = new GUIButton(
                    new RectTransform(new Vector2(0.15f, 1f), top.RectTransform), names[idx]);
                _tabs[i].OnClicked = (b, d) => { _tab = idx; _expanded = null; Refresh(); return true; };
            }

            var sf = new GUIFrame(new RectTransform(new Vector2(0.4f, 1f), top.RectTransform), style: null);
            _searchBox = new GUITextBox(new RectTransform(Vector2.One, sf.RectTransform), "");
            _searchBox.OnTextChanged += (b, t) => { _search = t ?? ""; Refresh(); return true; };

            var cl = new GUIButton(new RectTransform(new Vector2(0.09f, 1f), top.RectTransform), "X");
            cl.Color = new Color(100, 100, 120);
            cl.OnClicked = (b, d) => { CloseWindow(); return true; };

            var lf = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.86f), content.RectTransform, Anchor.BottomCenter), style: null);
            _list = new GUIListBox(new RectTransform(Vector2.One, lf.RectTransform));
            _list.Color = new Color(18, 22, 32);

            _uiSession++;
            CoroutineManager.StartCoroutine(UiLoop(_uiSession));
            Refresh();
        }

        private static void CloseWindow()
        {
            _window?.Close();
            _window = null;
            _list = null;
            _searchBox = null;
            _tabs = new GUIButton[3];
            _expanded = null;
            _uiSession++;
        }

        private static void Refresh()
        {
            try
            {
                if (_list == null) { return; }
                _list.Content.ClearChildren();

                for (int i = 0; i < 3; i++)
                {
                    if (_tabs[i] != null) { _tabs[i].Color = i == _tab ? TabOn : TabOff; }
                }

                var chars = Filtered().ToList();
                if (chars.Count == 0)
                {
                    var e = new GUITextBlock(
                        new RectTransform(new Vector2(1f, 0.06f), _list.Content.RectTransform),
                        "Нет существ в категории", textAlignment: Alignment.Center,
                        textColor: Color.Gray)
                    { CanBeFocused = false };
                    return;
                }

                foreach (var c in chars)
                {
                    AddCharRow(c);
                    if (_expanded == c) { AddInvRows(c); }
                }
            }
            catch (Exception e) { LuaCsLogger.LogError("[InvViewer] Refresh: " + e.Message); }
        }

        private static void AddCharRow(Character c)
        {
            string name = "???";
            try { name = c.DisplayName ?? c.Name ?? name; } catch { }
            bool pl = false;
            try { pl = c.IsPlayer; } catch { }

            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.07f), _list.Content.RectTransform), style: null);
            row.Color = _expanded == c ? new Color(50, 60, 40) : new Color(32, 40, 55);

            var lay = new GUILayoutGroup(
                new RectTransform(new Vector2(0.985f, 0.9f), row.RectTransform, Anchor.Center),
                isHorizontal: true)
            { RelativeSpacing = 0.01f };

            var exp = new GUIButton(
                new RectTransform(new Vector2(0.06f, 1f), lay.RectTransform),
                _expanded == c ? "v" : ">");
            exp.Color = new Color(70, 90, 120);
            Character cc = c;
            exp.OnClicked = (b, d) => { _expanded = _expanded == cc ? null : cc; Refresh(); return true; };

            var nb = new GUIButton(
                new RectTransform(new Vector2(0.4f, 1f), lay.RectTransform),
                (pl ? "[P] " : "") + name, textAlignment: Alignment.CenterLeft);
            nb.Color = new Color(45, 55, 75);
            nb.OnClicked = (b, d) => { _expanded = _expanded == cc ? null : cc; Refresh(); return true; };

            var st = new GUITextBlock(
                new RectTransform(new Vector2(0.16f, 1f), lay.RectTransform),
                StateOf(c), textAlignment: Alignment.Center);
            try { st.TextColor = c.IsDead ? BadCol : OkCol; } catch { }

            var hp = new GUITextBlock(
                new RectTransform(new Vector2(0.32f, 1f), lay.RectTransform),
                HpBar(c), textAlignment: Alignment.Center)
            { CanBeFocused = false };
            hp.TextColor = new Color(160, 220, 160);
        }

        private static void AddInvRows(Character c)
        {
            var hdr = new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.05f), _list.Content.RectTransform),
                "  ИНВЕНТАРЬ:", textAlignment: Alignment.CenterLeft,
                textColor: Color.Orange)
            { CanBeFocused = false };

            var items = new List<Item>();
            try
            {
                var inv = c.Inventory;
                if (inv != null)
                {
                    foreach (var it in inv.AllItemsMod)
                    {
                        if (it == null || it.Removed || it.HiddenInGame) { continue; }
                        items.Add(it);
                        if (items.Count >= 40) { break; }
                    }
                }
            }
            catch (Exception e) { LuaCsLogger.LogError("[InvViewer] inv: " + e.Message); }

            if (items.Count == 0)
            {
                var e0 = new GUITextBlock(
                    new RectTransform(new Vector2(1f, 0.05f), _list.Content.RectTransform),
                    "  (пусто)", textAlignment: Alignment.CenterLeft, textColor: Color.Gray)
                { CanBeFocused = false };
                return;
            }

            foreach (var it in items) { AddItemRow(it); }
        }

        private static void AddItemRow(Item it)
        {
            var row = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.065f), _list.Content.RectTransform), style: null);
            row.Color = new Color(24, 30, 42);

            var lay = new GUILayoutGroup(
                new RectTransform(new Vector2(0.985f, 0.92f), row.RectTransform, Anchor.Center),
                isHorizontal: true)
            { RelativeSpacing = 0.01f };

            var iconFrame = new GUIFrame(
                new RectTransform(new Vector2(0.06f, 0.95f), lay.RectTransform), style: null);
            iconFrame.Color = Color.Transparent;
            try
            {
                var icon = it.Prefab.InventoryIcon ?? it.Prefab.Sprite;
                if (icon != null)
                {
                    var img = new GUIImage(
                        new RectTransform(new Vector2(0.85f, 0.85f), iconFrame.RectTransform, Anchor.Center),
                        icon, false);
                    try { img.Color = it.Prefab.InventoryIconColor; } catch { }
                    img.CanBeFocused = false;
                }
            }
            catch { }

            int stack = 1;
            try { stack = Math.Max(1, it.Count); } catch { }
            var nameTxt = new GUITextBlock(
                new RectTransform(new Vector2(0.46f, 1f), lay.RectTransform),
                it.Name + (stack > 1 ? " x" + stack : ""), textAlignment: Alignment.CenterLeft)
            { CanBeFocused = false };
            nameTxt.ToolTip = "ID: " + it.ID;

            string condTxt = "?";
            Color condCol = Color.White;
            try
            {
                float pct = it.MaxCondition <= 0 ? 1f : it.Condition / it.MaxCondition;
                condTxt = (int)(pct * 100) + "%";
                condCol = pct > 0.6f ? OkCol : pct > 0.25f ? Color.Orange : BadCol;
            }
            catch { }
            var cond = new GUITextBlock(
                new RectTransform(new Vector2(0.14f, 1f), lay.RectTransform),
                condTxt, textAlignment: Alignment.Center)
            { CanBeFocused = false };
            cond.TextColor = condCol;

            var idTxt = new GUITextBlock(
                new RectTransform(new Vector2(0.14f, 1f), lay.RectTransform),
                "#" + it.ID, textAlignment: Alignment.Center)
            { CanBeFocused = false };
            idTxt.TextColor = new Color(130, 150, 190);
        }

        private static System.Collections.Generic.IEnumerable<CoroutineStatus> UiLoop(int session)
        {
            double next = 0.0;
            while (_window != null && session == _uiSession)
            {
                yield return CoroutineStatus.Running;
                if (Timing.TotalTime < next) { continue; }
                next = Timing.TotalTime + 2.0;

                if (_expanded != null)
                {
                    try { if (_expanded.Removed) { _expanded = null; Refresh(); } } catch { }
                }
            }
        }
    }
}
