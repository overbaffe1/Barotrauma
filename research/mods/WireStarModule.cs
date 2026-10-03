using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  WIRE DRAW — режим on/off: активировал → каждый ЛКМ-клик ставит
    //  ОДИН узел под курсором, провод рисует линию под курсором.
    //  Легитный клиентский путь: CreateClientEvent(ClientEventData(k))
    //  по одному узлу, сервер добавляет без клампов (находка 447)
    //  и реплицирует всем. Опрос кликов — игро́вая корутина
    //  (CoroutineManager.StartCoroutine, Running = следующий кадр).
    // ============================================================
    public class WireStarModule : CSModuleBase
    {
        public override string Id   => "wire_star";
        public override string Name => "Wire Draw";
        public override string Description =>
            "Режим рисования проводом.\n\n" +
            "• Нажми модуль = режим ВКЛ (ещё раз = ВЫКЛ)\n" +
            "• В режиме каждый ЛКМ-клик ставит узел под курсором\n" +
            "• Провод рисует линию между узлами\n" +
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
                GUI.AddMessage("[WireDraw] РЕЖИМ ВКЛ — ЛКМ по экрану = узел (" + wireItem.Name + ")", Color.Lime);
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
        private static IEnumerator DrawLoop()
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
                    PlaceNode();
                }

                yield return CoroutineStatus.Running; // следующий кадр
            }
        }

        private static void PlaceNode()
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

            // узлы в пространстве субмарины (draw = node + DrawPos + HiddenSubPos)
            Vector2 pos = cam.ScreenToWorld(PlayerInput.MousePosition);
            Submarine refSub = _wireItem.Submarine;
            if (refSub != null)
            {
                pos -= refSub.DrawPosition + refSub.HiddenSubPosition;
            }

            // первый узел: стартуем от предмета-провода (его Position тоже
            // мировая — конвертим в N-пространство так же, как клик)
            if (nodes.Count == 0)
            {
                Vector2 startPos = _wireItem.Position;
                Submarine startSub = _wireItem.Submarine;
                if (startSub != null)
                {
                    startPos -= startSub.DrawPosition + startSub.HiddenSubPosition;
                }
                nodes.Add(startPos);
                SendEvent(_wireItem, _wire, nodes.Count, send);
            }

            nodes.Add(pos);
            SendEvent(_wireItem, _wire, nodes.Count, send);
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
