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

        // Пошаговая отрисовка, темп 1 узел/сек (просьба юзера): 11 ивентов
        // одним кадром затираются эхом, ваниль шлёт узлы в темпе перетаскивания.
        // Планировщик — CoroutineManager (движок зовёт экшены в главном потоке;
        // CSModuleBase.Update фреймворком не вызывается).
        private const float StepInterval = 1.0f;

        // Незавершённые шаги прошлых рисований само-глушатся: CoroutineHandle
        // игры internal (по имени не достать), поэтому каждый шаг проверяет
        // номер сессии. Иначе два потока перемешиваются (сброс очищает nodes,
        // пока события прошлой звезды в полёте → nodes.Last() кидает
        // "no elements" → вся пачка ивентов ломается → провод исчезает).
        private static int _drawSession;

        public override string GetLabel() => "Wire Star ★";

        public override void OnClick()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("[WireStar] Только в мультиплеере (нужен сервер-получатель ивентов)", Color.Orange);
                return;
            }

            _drawSession++;

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

            List<Vector2> verts = StarVerts(center, StarRadius, InnerRatio);

            var nodes = nodesField?.GetValue(wire) as List<Vector2>;
            MethodInfo send = ResolveSend();
            if (nodes == null || send == null)
            {
                GUI.AddMessage("[WireStar] reflection не готов", Color.Red);
                return;
            }

            GUI.AddMessage("[WireStar] рисую ★ (" + verts.Count + " узлов, 1 узел/сек, " + wireItem.Name + ")...", Color.Lime);

            int session = _drawSession;
            int delayIdx = 0;

            // шаг 0: сброс старых узлов (nodeCount=0 → сервер RemoveRange всё)
            if (nodes.Count > 0)
            {
                ScheduleStep(wireItem, wire, nodes, send, 0, Vector2.Zero, 0, session, delayIdx);
                delayIdx++;
            }

            // шаги 1..N: по одному узлу в секунду
            for (int i = 0; i < verts.Count; i++)
            {
                ScheduleStep(wireItem, wire, nodes, send, 1, verts[i], i, session, delayIdx);
                delayIdx++;
            }
        }

        // Один шаг рисования, исполняется CoroutineManager'ом в своё время.
        // Устаревшая сессия (юзер кликнул снова) молча выходит.
        private static void ScheduleStep(Item item, Wire wire, List<Vector2> nodes,
            MethodInfo send, int mode, Vector2 vertex, int vertexIndex, int session, int delayIdx)
        {
            CoroutineManager.Invoke(() =>
            {
                try
                {
                    if (session != _drawSession) return;   // устаревший шаг
                    if (item == null || item.Removed || item.GetComponent<Wire>() != wire) return;

                    if (mode == 0)
                    {
                        // сброс: только если есть что сбрасывать
                        if (nodes.Count == 0) return;
                        nodes.Clear();
                        SendEvent(item, wire, 0, send);
                    }
                    else
                    {
                        // узел i добавляется только при ровно i узлах в списке;
                        // рассинхрон = тихо стоп (иначе nodes.Last() кидает
                        // "no elements" и валит всю пачку событий клиента)
                        if (nodes.Count != vertexIndex) return;
                        nodes.Add(vertex);
                        SendEvent(item, wire, nodes.Count, send);
                        wire.UpdateSections();
                    }
                }
                catch (Exception e)
                {
                    GUI.AddMessage("[WireStar] фейл: " + e.GetBaseException().Message, Color.Red);
                }
            }, delayIdx * StepInterval);
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
