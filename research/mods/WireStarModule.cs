using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    // ============================================================
    //  WIRE DRAW — провод рисует линию туда, куда кликнул.
    //  От позиции провода (или последней точки) к месту клика.
    //  Легитный клиентский путь: узлы по одному через
    //  CreateClientEvent(ClientEventData(k)), сервер добавляет
    //  без клампов (находка 447) и реплицирует всем.
    // ============================================================
    public class WireStarModule : CSModuleBase
    {
        public override string Id   => "wire_star";
        public override string Name => "Wire Draw";
        public override string Description =>
            "Провод рисует линию к месту клика.\n\n" +
            "• Нужен чистый разблокированный провод в инвентаре\n" +
            "• Первый клик — линия от провода к точке\n" +
            "• Каждый следующий клик продолжает линию от предыдущей точки\n" +
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

        private const float StepInterval = 0.05f; // 0.05с на промежуточный узел

        // сессия: новый клик глушит незавершённые шаги прошлой линии
        private static int _drawSession;

        public override string GetLabel() => "Wire Draw ✏";

        public override void OnClick()
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
            Wire wire = wireItem.GetComponent<Wire>();

            Camera cam = (Screen.Selected as GameScreen)?.Cam;
            if (cam == null) return;
            Vector2 target = cam.ScreenToWorld(PlayerInput.MousePosition);

            MethodInfo send = ResolveSend();
            if (send == null)
            {
                GUI.AddMessage("[WireDraw] reflection не готов", Color.Red);
                return;
            }

            // свежий список: эхо сервера заменяет объект, захватывать нельзя
            var nodes = nodesField?.GetValue(wire) as List<Vector2>;
            if (nodes == null)
            {
                GUI.AddMessage("[WireDraw] reflection: nodes не найдены", Color.Red);
                return;
            }

            // старт линии: уже нарисованное продолжаем, чистый провод — от предмета
            Vector2 start = nodes.Count > 0 ? nodes[nodes.Count - 1] : wireItem.Position;

            // узлы в пространстве субмарины (draw = node + DrawPos + HiddenSubPos)
            Submarine refSub = wireItem.Submarine;
            if (refSub != null)
            {
                target -= refSub.DrawPosition + refSub.HiddenSubPosition;
            }

            // дистанция в мире: старт может быть в старых координатах субмарины,
            // для простоты режем по модулю разницы (оба в N-пространстве)
            float dist = Vector2.Distance(start, target);
            if (dist < 1f)
            {
                GUI.AddMessage("[WireDraw] слишком близко", Color.Orange);
                return;
            }

            int session = ++_drawSession;

            // промежуточные узлы каждые ~50px, максимум 250 за клик (лимит 255)
            int steps = (int)(dist / 50f) + 1;
            steps = Math.Min(steps, 250);

            GUI.AddMessage("[WireDraw] линия " + (int)dist + "px, " + steps + " узлов", Color.Lime);

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector2 pos = start + (target - start) * t;
                ScheduleStep(wireItem, wire, send, pos, session, i * StepInterval);
            }
        }

        // Один узел, исполняется CoroutineManager'ом в своё время.
        private static void ScheduleStep(Item item, Wire wire, MethodInfo send,
            Vector2 vertex, int session, float delay)
        {
            CoroutineManager.Invoke(() =>
            {
                try
                {
                    if (session != _drawSession) return;   // устаревший шаг
                    if (item == null || item.Removed || item.GetComponent<Wire>() != wire) return;

                    // СВЕЖИЙ список каждый раз — эхо заменяет объект списка
                    var nodes = nodesField?.GetValue(wire) as List<Vector2>;
                    if (nodes == null || nodes.Count == 0) return; // пусто = рассинхрон, стоп

                    nodes.Add(vertex);
                    SendEvent(item, wire, nodes.Count, send);
                    wire.UpdateSections();
                }
                catch (Exception e)
                {
                    GUI.AddMessage("[WireDraw] фейл: " + e.GetBaseException().Message, Color.Red);
                }
            }, delay);
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

        public override void Dispose() { base.Dispose(); }
    }
}
