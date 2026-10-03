using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  WIRE DRAW — режим on/off: активировал → каждый ЛКМ-клик
    //  ставит узел под курсором, и провод РИСУЕТ ПРЯМУЮ от
    //  предыдущей точки к новой (узлы распределяются по прямой,
    //  но рисуются за один кадр — без пошагового ползания).
    //  Легитный клиентский путь: CreateClientEvent(ClientEventData(k))
    //  по одному узлу, сервер добавляет без клампов (находка 447)
    //  и реплицирует всем.
    // ============================================================
    public class WireStarModule : CSModuleBase
    {
        public override string Id   => "wire_star";
        public override string Name => "Wire Draw";
        public override string Description =>
            "Режим рисования проводом.\n\n" +
            "• Нажми модуль = режим ВКЛ (ещё раз = ВЫКЛ)\n" +
            "• ЛКМ-клик = прямая линия от предыдущей точки к курсору\n" +
            "• (первый клик — линия от самого провода)\n" +
            "• Нужен чистый разблокированный провод в инвентаре\n" +
            "• Видно всем игрокам";
        public override string Category => "fun";

        // Wire.nodes — private List<Vector2>
        private static readonly FieldInfo nodesField = typeof(Wire).GetField(
            "nodes", BindingFlags.NonPublic | BindingFlags.Instance);

        // Wire.ClientEventData — private nested struct (int nodeCount)
        private static readonly Type clientEventData = typeof(Wire).GetNestedType(
            "ClientEventData", BindingFlags.NonPublic);

        // Item.CreateClientEvent<T>(T ic, ItemComponent.IEventData extraData)
        private static MethodInfo _createClientEvent;

        private static bool _active;
        private static Item _wireItem;
        private static Wire _wire;

        public override string GetLabel() => _active ? "Wire Draw ✏ [ВКЛ]" : "Wire Draw ✏";

        public override void OnClick()
        {
            if (!_active)
            {
                if (GameMain.Client == null)
                {
                    GUI.AddMessage("[WireDraw] Только в мультиплеере", Color.Orange);
                    return;
                }

                Item wireItem = FindWire();
                if (wireItem == null)
                {
                    GUI.AddMessage("[WireDraw] Возьми чистый разблокированный провод в инвентарь", Color.Orange);
                    return;
                }

                _wireItem = wireItem;
                _wire = wireItem.GetComponent<Wire>();
                _active = true;
                CoroutineManager.StartCoroutine(DrawLoop());
                GUI.AddMessage("[WireDraw] РЕЖИМ ВКЛ — ЛКМ: прямая от прошлой точки к курсору", Color.Lime);
            }
            else
            {
                Deactivate("РЕЖИМ ВЫКЛ");
            }
        }

        private static void Deactivate(string msg)
        {
            _active = false;
            _wireItem = null;
            _wire = null;
            GUI.AddMessage("[WireDraw] " + msg, Color.Orange);
        }

        // Живёт, пока _active; тикает каждый кадр игровым планировщиком.
        // Сигнатура: IEnumerable<CoroutineStatus> (не IEnumerator!).
        private static IEnumerable<CoroutineStatus> DrawLoop()
        {
            while (_active)
            {
                // провод удалён/выкинут/заменён — глушим режим
                if (_wireItem == null || _wireItem.Removed || _wireItem.ParentInventory == null ||
                    _wireItem.GetComponent<Wire>() != _wire)
                {
                    Deactivate("провод недоступен — режим ВЫКЛ");
                    yield break;
                }

                if (PlayerInput.PrimaryMouseButtonClicked())
                {
                    PlaceLine();
                }

                yield return CoroutineStatus.Running; // следующий кадр
            }
        }

        // ЛКМ: прямая от последней точки к курсору. Все промежуточные
        // узлы ставятся СЕЙЧАС (одним кадром) — линия появляется сразу
        // целиком, без «ползания» по кускам.
        private static void PlaceLine()
        {
            Camera cam = (Screen.Selected as GameScreen)?.Cam;
            if (cam == null) return;

            MethodInfo send = ResolveSend();
            if (send == null) { GUI.AddMessage("[WireDraw] reflection не готов", Color.Red); return; }

            // СВЕЖИЙ список каждый раз: эхо сервера заменяет объект списка
            var nodes = nodesField?.GetValue(_wire) as List<Vector2>;
            if (nodes == null) return;

            // лимит игры 255 узлов на провод
            if (nodes.Count >= 250)
            {
                Deactivate("провод заполнен (250 узлов) — режим ВЫКЛ, возьми новый");
                return;
            }

            // клик → в N-пространство субмарины (draw = node + DrawPos + HiddenSubPos)
            Vector2 target = cam.ScreenToWorld(PlayerInput.MousePosition);
            Submarine refSub = _wireItem.Submarine;
            if (refSub != null)
            {
                target -= refSub.DrawPosition + refSub.HiddenSubPosition;
            }

            // старт: последний узел, либо сам провод (первый клик)
            Vector2 start;
            if (nodes.Count > 0)
            {
                start = nodes[nodes.Count - 1];
            }
            else
            {
                start = _wireItem.Position;
                Submarine startSub = _wireItem.Submarine;
                if (startSub != null)
                {
                    start -= startSub.DrawPosition + startSub.HiddenSubPosition;
                }
            }

            float dist = Vector2.Distance(start, target);
            if (dist < 1f) return;

            // промежуточные узлы каждые ~50px, с запасом до лимита 255
            int free = 250 - nodes.Count;
            int steps = Math.Min((int)(dist / 50f), free);
            if (steps < 1) steps = 1;

            for (int i = 1; i <= steps; i++)
            {
                nodes.Add(start + (target - start) * ((float)i / steps));
                SendEvent(_wireItem, _wire, nodes.Count, send);
            }

            _wire.UpdateSections();
        }

        // ===== ПОИСК ПРОВОДА =====
        // Предпочитаем ПОЛНОСТЬЮ отключённые провода.
        private static Item FindWire()
        {
            Character me = Character.Controlled;
            if (me?.Inventory == null) return null;

            Item fallback = null;
            foreach (Item it in me.Inventory.AllItems)
            {
                if (it == null || it.Removed) continue;
                Wire w = it.GetComponent<Wire>();
                if (w == null || w.Locked) continue;
                if (w.Connections[0] == null && w.Connections[1] == null) return it;
                fallback ??= it;
            }
            return fallback;
        }

        private static void SendEvent(Item item, Wire wire, int nodeCount, MethodInfo send)
        {
            object data = Activator.CreateInstance(clientEventData, nodeCount);
            send.MakeGenericMethod(typeof(Wire)).Invoke(item, new object[] { wire, data });
        }

        private static MethodInfo ResolveSend()
        {
            if (_createClientEvent != null) return _createClientEvent;
            foreach (MethodInfo m in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "CreateClientEvent" || !m.IsGenericMethodDefinition) continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 2 && typeof(ItemComponent.IEventData).IsAssignableFrom(p[1].ParameterType))
                {
                    _createClientEvent = m;
                    break;
                }
            }
            return _createClientEvent;
        }

        public override void Dispose()
        {
            _active = false;
            base.Dispose();
        }
    }
}
