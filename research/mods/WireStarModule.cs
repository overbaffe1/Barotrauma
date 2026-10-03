using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  WIRE STAR — рисует звезду проводом в месте курсора.
    //  Легитный клиентский путь (как при перетаскивании провода):
    //  локально растим список узлов → CreateClientEvent(ClientEventData(k))
    //  на каждый узел → сервер (Wire.ServerEventRead) добавляет по одному
    //  узлу за ивент (nodeCount + lastNodePos) и реплицирует ВСЕМ клиентам.
    //  Позиции узлов сервером не клампятся (находка 447) — рисуй где хочешь.
    //  Требования: провод в СВОЁМ инвентаре (не заперт), ты жив.
    // ============================================================
    public class WireStarModule : CSModuleBase
    {
        public override string Id   => "wire_star";
        public override string Name => "Wire Star";
        public override string Description =>
            "Рисует маленькую звезду проводом там, где курсор.\n\n" +
            "• Нужен разблокированный провод в твоём инвентаре\n" +
            "• Узлы строит легитными ивентами (nodeCount+позиция),\n" +
            "  сервер реплицирует звезду всем игрокам\n" +
            "• Позиции узлов сервер не проверяет (447)\n" +
            "• Работает только в мультиплеере";
        public override string Category => "fun";

        // Wire.nodes — private List<Vector2>
        private static readonly FieldInfo nodesField = typeof(Wire).GetField(
            "nodes", BindingFlags.NonPublic | BindingFlags.Instance);

        // Wire.ClientEventData — private nested struct (int nodeCount), только в клиентской сборке
        private static readonly Type clientEventData = typeof(Wire).GetNestedType(
            "ClientEventData", BindingFlags.NonPublic);

        // Item.CreateClientEvent<T>(T ic, ItemComponent.IEventData extraData)
        private static MethodInfo _createClientEvent;

        private const float StarRadius = 45f;   // внешний радиус звезды (px)
        private const float InnerRatio = 0.42f; // внутренний радиус = внешний * ratio

        // Пошаговая отрисовка: 11 ивентов одним кадром затираются эхо-состояниями
        // (echo k обрезает локальный список до k, пока следующие уже ушли).
        // Ваниль шлёт узлы по одному при перетаскивании — повторяем этот темп.
        private const double StepInterval = 0.1; // сек между узлами

        private static Item _drawItem;
        private static Wire _drawWire;
        private static List<Vector2> _pending;   // вершины звезды
        private static int _step;                // 0=сброс, 1..N=узлы
        private static double _nextStepAt;

        public override string GetLabel() => "Wire Star ★";

        public override void OnClick()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[WireStar] Только в мультиплеере (нужен сервер-получатель ивентов)", Color.Orange);
                return;
            }

            Item wireItem = FindWire();
            if (wireItem == null)
            {
                GUI.AddMessage("[WireStar] Возьми разблокированный провод в инвентарь", Color.Orange);
                return;
            }
            Wire wire = wireItem.GetComponent<Wire>();

            Camera cam = (Screen.Selected as GameScreen)?.Cam;
            if (cam == null) return;
            Vector2 center = cam.ScreenToWorld(PlayerInput.MousePosition);

            // Узлы провода хранятся в пространстве СУБМАРИНЫ, а рисуются как
            // nodePos + sub.DrawPosition + sub.HiddenSubPosition (Wire.GetDrawOffset).
            // Инвертируем сдвиг: иначе звезда улетает на начало координат мира.
            // В море (субы нет) сдвиг нулевой — мировые координаты как есть.
            Submarine refSub = wireItem.Submarine;
            if (refSub != null)
            {
                center -= refSub.DrawPosition + refSub.HiddenSubPosition;
            }

            _drawItem = wireItem;
            _drawWire = wire;
            _pending = StarVerts(center, StarRadius, InnerRatio);
            _step = 0;
            _nextStepAt = 0.0; // первый шаг сразу
            GUI.AddMessage("[WireStar] рисую ★ (" + _pending.Count + " узлов, " + wireItem.Name + ")...", Color.Lime);
        }

        public override void Update()
        {
            if (_pending == null || _drawWire == null) return;
            if (Timing.TotalTime < _nextStepAt) return;
            _nextStepAt = Timing.TotalTime + StepInterval;

            // провод удалён/заменён посреди рисования — отменяем
            if (_drawItem == null || _drawItem.Removed ||
                _drawItem.GetComponent<Wire>() != _drawWire)
            { Abort(); return; }

            var nodes = nodesField?.GetValue(_drawWire) as List<Vector2>;
            MethodInfo send = ResolveSend();
            if (nodes == null || send == null) { Abort(); return; }

            try
            {
                if (_step == 0)
                {
                    // сброс старых узлов: nodeCount=0 → сервер RemoveRange(всё)
                    nodes.Clear();
                    SendEvent(_drawItem, _drawWire, 0, send);
                }
                else
                {
                    // по одному узлу за шаг: локальный список растёт,
                    // ивент несёт nodeCount=k + позицию последнего узла
                    int i = _step - 1;
                    if (i >= _pending.Count) { Abort(); return; }
                    nodes.Add(_pending[i]);
                    SendEvent(_drawItem, _drawWire, nodes.Count, send);
                    _drawWire.UpdateSections();
                }

                _step++;
                if (_step > _pending.Count)
                {
                    _drawWire.UpdateSections();
                    GUI.AddMessage("[WireStar] ★ готова", Color.Lime);
                    _pending = null;
                }
            }
            catch (Exception e)
            {
                GUI.AddMessage("[WireStar] фейл: " + e.GetBaseException().Message, Color.Red);
                Abort();
            }
        }

        private static void Abort()
        {
            _pending = null;
            _drawItem = null;
            _drawWire = null;
        }

        // ===== ПОИСК ПРОВОДА =====
        private static Item FindWire()
        {
            Character me = Character.Controlled;
            if (me?.Inventory == null) return null;
            foreach (Item it in me.Inventory.AllItems)
            {
                if (it == null || it.Removed) continue;
                Wire w = it.GetComponent<Wire>();
                if (w != null && !w.Locked) return it;
            }
            return null;
        }

        // ===== ГЕОМЕТРИЯ =====
        private static List<Vector2> StarVerts(Vector2 center, float radius, float innerRatio)
        {
            var verts = new List<Vector2>(10);
            for (int i = 0; i < 10; i++)
            {
                // старт с верхнего луча, шаг 36°, чередуем внешний/внутренний радиус
                double ang = Math.PI / 2.0 + i * (Math.PI / 5.0);
                float r = (i % 2 == 0) ? radius : radius * innerRatio;
                verts.Add(center + new Vector2(
                    (float)(Math.Cos(ang) * r),
                    (float)(Math.Sin(ang) * r)));
            }
            return verts;
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

        public override void Dispose() { base.Dispose(); }
    }
}
