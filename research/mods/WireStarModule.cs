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

            List<Vector2> verts = StarVerts(center, StarRadius, InnerRatio);

            string err;
            if (TryDraw(wireItem, wire, verts, out err))
                GUI.AddMessage("[WireStar] ★ нарисована (" + verts.Count + " узлов, " + wireItem.Name + ")", Color.Lime);
            else
                GUI.AddMessage("[WireStar] Фейл: " + err, Color.Red);
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

        // ===== ОТПРАВКА =====
        private static bool TryDraw(Item item, Wire wire, List<Vector2> verts, out string err)
        {
            err = null;
            if (nodesField == null) { err = "reflection: nodes не найдены"; return false; }
            if (clientEventData == null) { err = "reflection: ClientEventData не найден"; return false; }

            var nodes = nodesField.GetValue(wire) as List<Vector2>;
            if (nodes == null) { err = "wire.nodes == null"; return false; }

            MethodInfo send = ResolveSend();
            if (send == null) { err = "reflection: CreateClientEvent не найден"; return false; }

            try
            {
                // 1) сброс старых узлов: nodeCount=0 → сервер RemoveRange(всё)
                if (nodes.Count > 0)
                {
                    nodes.Clear();
                    SendEvent(item, wire, 0, send);
                }

                // 2) строим звезду по одному узлу: локальный список растёт,
                //    каждый ивент несёт nodeCount=k + позицию последнего узла;
                //    сервер: nodeCount > nodes.Count → добавляет узел
                foreach (Vector2 v in verts)
                {
                    nodes.Add(v);
                    SendEvent(item, wire, nodes.Count, send);
                }

                // 3) мгновенная отрисовка у себя (серверный эхо-апдейт придёт и так)
                wire.UpdateSections();
                return true;
            }
            catch (Exception e)
            {
                err = e.GetBaseException().Message;
                return false;
            }
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

        public override void Update() { }

        public override void Dispose() { base.Dispose(); }
    }
}
