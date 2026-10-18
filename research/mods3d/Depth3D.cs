// ============================================================================
//  Depth3D.cs v2.0 — НАСТОЯЩЕЕ 3D-ПРОСТРАНСТВО: коридоры, стены, перспектива
//  CSHUB: Modules/Depth3D.cs | F5=3D | F9=режим | F6=стиль | LMB=use
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Depth3D
{
    public sealed class Depth3DMod : ACsMod
    {
        private const float MouseSens = 0.0027f;
        private const float ViewDist = 3000f;
        private const float RoomDepth = 300f;
        private const float WallHeight = 100f;
        private const string Prefix = "d3d.";

        public enum WorldInterp { BoxWorld = 0, WireFrame = 1, Solid = 2, Xray = 3, Count = 4 }
        private enum ViewStyle { Normal, Full, Xray }

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<HookRegistration> hooks = new List<HookRegistration>();
        private WorldInterp interp = WorldInterp.Solid;
        private ViewStyle style = ViewStyle.Full;
        private bool enabled, stopped, ready;
        private bool mouseCaptured, discardNext;
        private bool showHints, clickPending, clickArmed, interactAvail, warnCursor, warmup = true;
        private Character owner;
        private Vector2 savedCursor;
        private float yaw, pitch;
        private Point savedMouse;
        private ButtonState prevLeft;
        private Point clickPos;
        private KeyboardState prevKb;
        private int floorLine;
        private SpriteBatch sb;
        private Texture2D whiteTex;

        private static string InterpName(WorldInterp w) => w switch
        {
            WorldInterp.BoxWorld => "BOX WORLD",
            WorldInterp.WireFrame => "WIREFRAME",
            WorldInterp.Solid => "SOLID",
            WorldInterp.Xray => "XRAY",
            _ => "?"
        };
        private static string StyleName(ViewStyle s) => s switch
        {
            ViewStyle.Normal => "NORMAL",
            ViewStyle.Full => "FULL",
            ViewStyle.Xray => "XRAY",
            _ => "?"
        };

        public Depth3DMod()
        {
            LoadPersist(); prevKb = Keyboard.GetState();
            try
            {
                var drawMap = FindM(typeof(GameScreen), "DrawMap", 3);
                var draw = FindM(typeof(GameScreen), "Draw", 3);
                if (drawMap == null || draw == null) throw new MissingMethodException("DrawMap/Draw");

                AddHook("map", drawMap, (inst, args) =>
                {
                    if (stopped || !enabled) return null;
                    if (!HasCtx()) { Disable(false); return null; }
                    try { Render(); } catch (Exception ex) { Fail("render", ex); }
                    return null;
                });
                AddHook("hud", draw, (inst, args) =>
                {
                    if (stopped || !enabled || !HasCtx()) return null;
                    try { Overlay(); } catch (Exception ex) { Fail("overlay", ex); }
                    return null;
                });
                var curM = FindM(typeof(Character), "UpdateLocalCursor", 1);
                if (curM != null)
                    AddHook("cursor", curM, (inst, args) =>
                    {
                        if (stopped || !enabled || !ReferenceEquals(inst, owner)) return null;
                        try { Aim(); } catch (Exception ex) { Fail("aim", ex); }
                        return null;
                    });
                else warnCursor = true;
                var ctrlM = FindM(typeof(Character), "ControlLocalPlayer", 3);
                if (ctrlM != null)
                    AddHook("interact", ctrlM, (inst, args) =>
                    {
                        if (stopped || !enabled || !ReferenceEquals(inst, owner)) return null;
                        try { Commit(); } catch (Exception ex) { Fail("interact", ex); }
                        return null;
                    });

                GameMain.LuaCs.Hook.Add("think", Prefix + "tick", (args) =>
                {
                    if (stopped) return null;
                    try { Tick(); } catch (Exception ex) { Fail("tick", ex); }
                    return null;
                });
                ready = true;
                Log($"Ready. F5=3D | F9={InterpName(interp)}", Color.Cyan);
            }
            catch (Exception ex) { stopped = true; RemoveHooks(); Log("Init: " + ex.Message, Color.OrangeRed); }
        }
        public override void Stop() { Disable(false); RemoveHooks(); }
        public override void Dispose() { Disable(false); RemoveHooks(); }

        private bool HasCtx() => Screen.Selected is GameScreen && Character.Controlled is { IsDead: false, Removed: false };
        private bool UiOwns() => GUI.MouseOn is GUIComponent || GUI.KeyboardDispatcher?.Subscriber != null;
        private bool OnUi() => GUI.MouseOn is GUIComponent;
        private bool CrewGui() => Character.Controlled?.SelectedItem != null;

        private void Tick()
        {
            var kb = Keyboard.GetState(); var ms = Mouse.GetState();
            bool f5 = kb.IsKeyDown(Keys.F5) && prevKb.IsKeyUp(Keys.F5);
            bool f6 = kb.IsKeyDown(Keys.F6) && prevKb.IsKeyUp(Keys.F6);
            bool f7 = kb.IsKeyDown(Keys.F7) && prevKb.IsKeyUp(Keys.F7);
            bool f8 = kb.IsKeyDown(Keys.F8) && prevKb.IsKeyUp(Keys.F8);
            bool f9 = kb.IsKeyDown(Keys.F9) && prevKb.IsKeyUp(Keys.F9);
            bool b = PlayerInput.KeyHit(Keys.B);
            bool mP = ms.LeftButton == ButtonState.Pressed && prevLeft == ButtonState.Released;
            bool mR = ms.LeftButton == ButtonState.Released && prevLeft == ButtonState.Pressed;
            prevKb = kb; prevLeft = ms.LeftButton;
            if (enabled && !HasCtx()) Disable(false);
            if (!GameMain.WindowActive) { mouseCaptured = false; showHints = false; clickPending = false; clickArmed = false; discardNext = true; return; }
            if (f5 && ready) { if (enabled) Disable(true); else if (!UiOwns()) Enable(); }
            if (!enabled) return;
            if (f9 && !UiOwns()) { interp = (WorldInterp)(((int)interp + 1) % (int)WorldInterp.Count); Persist(); Log("MODE: " + InterpName(interp), Color.Cyan); }
            if (f7 && !UiOwns()) Log("XML reload: not in v2.", Color.Orange);
            if (f8 && !UiOwns()) ExportCSV();
            if (b) { floorLine = (floorLine + 1) % 3; Persist(); }
            bool ui = UiOwns() || CrewGui();
            if (f6 && !UiOwns()) { style = (ViewStyle)(((int)style + 1) % 3); Log("STYLE: " + StyleName(style), Color.Cyan); }
            showHints = kb.IsKeyDown(Keys.LeftAlt) && !ui;
            bool capture = !ui && !showHints;
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
            var ptr = capture ? new Point(cx, cy) : new Point(ms.X, ms.Y);
            bool worldIn = !ui && !OnUi() && (showHints || !PlayerInput.KeyDown(InputType.Aim));
            if (!worldIn) { clickArmed = false; clickPending = false; }
            if (mP && worldIn && interactAvail) clickArmed = true;
            if (mR && clickArmed && worldIn) { clickPos = ptr; clickPending = true; clickArmed = false; }
            if (!capture) { mouseCaptured = false; discardNext = true; if (warnCursor && !ui) Aim(); return; }
            if (!mouseCaptured || discardNext) { mouseCaptured = true; discardNext = false; Mouse.SetPosition(cx, cy); if (warnCursor) Aim(); return; }
            float dx = MathHelper.Clamp(ms.X - cx, -250f, 250f);
            float dy = MathHelper.Clamp(ms.Y - cy, -250f, 250f);
            yaw = MathHelper.WrapAngle(yaw + dx * MouseSens);
            pitch = MathHelper.Clamp(pitch - dy * MouseSens, -1.38f, 1.38f);
            Mouse.SetPosition(cx, cy);
            if (warnCursor) Aim();
        }

        private void Enable()
        {
            var ch = Character.Controlled;
            if (!(Screen.Selected is GameScreen) || ch == null || ch.IsDead || ch.Removed) { Log("Need character.", Color.Orange); return; }
            owner = ch; savedCursor = ch.CursorPosition;
            var ms = Mouse.GetState(); savedMouse = new Point(ms.X, ms.Y); prevLeft = ms.LeftButton;
            clickPending = false; showHints = false; clickArmed = false;
            yaw = (ch.AnimController?.Dir ?? 1f) >= 0f ? 0f : MathHelper.Pi;
            pitch = 0f; enabled = true; warmup = true; discardNext = true;
            Log("ON / " + InterpName(interp), Color.LimeGreen);
        }
        private void Disable(bool announce)
        {
            if (!enabled) return;
            enabled = false; clickPending = false; showHints = false; clickArmed = false; mouseCaptured = false; discardNext = true;
            if (owner != null && !owner.Removed && ReferenceEquals(owner, Character.Controlled)) owner.CursorPosition = savedCursor;
            owner = null;
            if (GameMain.WindowActive) Mouse.SetPosition(savedMouse.X, savedMouse.Y);
            if (announce) Log("OFF / 2D.", Color.Orange);
        }
        private void Aim() { if (owner == null || owner.Removed) return; var dir = new Vector2((float)Math.Cos(yaw), (float)Math.Sin(yaw)); owner.CursorPosition = owner.WorldPosition + dir * 500f; }
        private void Commit()
        {
            if (!clickPending || owner == null || owner.Removed) return;
            clickPending = false;
            Item best = null; float bestD = float.MaxValue;
            foreach (var item in Item.ItemList)
            {
                if (item == null || item.Removed || item.ParentInventory != null) continue;
                var delta = item.WorldPosition - owner.WorldPosition;
                float d = delta.Length();
                if (d > ViewDist || d > bestD) continue;
                float ang = (float)Math.Atan2(delta.Y, delta.X);
                if (Math.Abs(MathHelper.WrapAngle(ang - yaw)) < 0.4f) { bestD = d; best = item; }
            }
            if (best != null && !best.Removed)
            {
                float dd = Vector2.Distance(owner.WorldPosition, best.WorldPosition);
                if (dd <= 300f) { try { best.Use(1.0f / 60.0f, owner); } catch (Exception ex) { DebugConsole.ThrowError("[D3D] " + ex.Message); } }
            }
        }

        // ==== 3D РЕНДЕР ====
        private float CosYaw, SinYaw, ProjScale;
        private int ScrW, ScrH, ScrCX, ScrCY;
        private Vector3 CamPos;

        private void Render()
        {
            if (owner == null) return;
            EnsureSb();
            CosYaw = (float)Math.Cos(yaw); SinYaw = (float)Math.Sin(yaw);
            ScrW = GameMain.GraphicsWidth; ScrH = GameMain.GraphicsHeight;
            ScrCX = ScrW / 2; ScrCY = ScrH / 2;
            CamPos = new Vector3(owner.WorldPosition.X, owner.WorldPosition.Y, 0f);
            float tanFov = (float)Math.Tan(MathHelper.ToRadians(78f) / 2f);
            ProjScale = ScrH / 2f / tanFov;

            sb.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied);
            try
            {
                // Собираем стены из Hull-ов
                var currentHull = Hull.FindHull(owner.WorldPosition, null);
                float halfD = RoomDepth / 2f;
                var quads = new List<(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, float dist)>();

                foreach (var hull in Hull.HullList)
                {
                    if (hull == null || hull.Removed) continue;
                    var wr = hull.WorldRect;
                    if (wr.Width < 20 || wr.Height < 20) continue;
                    var hc = new Vector2(wr.X + wr.Width / 2f, wr.Y + wr.Height / 2f);
                    float hd = Vector2.Distance(hc, new Vector2(CamPos.X, CamPos.Y));
                    if (hd > ViewDist + wr.Width) continue;
                    bool isCur = hull == currentHull;
                    bool isAdj = false;
                    if (!isCur && currentHull != null)
                    { foreach (var conn in currentHull.Connections) { if (conn.OtherHull(currentHull) == hull) { isAdj = true; break; } } }
                    if (!isCur && !isAdj && hd > ViewDist / 2f) continue;

                    float x0 = wr.X, y0 = wr.Y, x1 = wr.X + wr.Width, y1 = wr.Y + wr.Height;
                    Color wc, fc, cc;
                    if (isCur)
                    {
                        wc = interp == WorldInterp.Solid ? Color.SlateGray * 0.6f : interp == WorldInterp.Xray ? Color.Cyan * 0.06f : interp == WorldInterp.WireFrame ? Color.Cyan * 0.4f : Color.CadetBlue * 0.3f;
                        fc = interp == WorldInterp.Solid ? Color.SaddleBrown * 0.5f : Color.DarkSlateGray * 0.4f;
                        cc = interp == WorldInterp.Solid ? Color.DimGray * 0.4f : Color.Gray * 0.12f;
                    }
                    else { wc = Color.DimGray * 0.15f; fc = Color.Brown * 0.1f; cc = Color.Gray * 0.08f; }

                    // стены: left, right, far, floor, ceiling
                    // left: x = x0
                    quads.Add((new Vector3(x0, y1, -halfD), new Vector3(x0, y1, halfD), new Vector3(x0, y0, halfD), new Vector3(x0, y0, -halfD), wc, hd));
                    // right: x = x1
                    quads.Add((new Vector3(x1, y1, -halfD), new Vector3(x1, y1, halfD), new Vector3(x1, y0, halfD), new Vector3(x1, y0, -halfD), wc, hd));
                    // far: y = y0
                    quads.Add((new Vector3(x0, y0, -halfD), new Vector3(x1, y0, -halfD), new Vector3(x1, y0, halfD), new Vector3(x0, y0, halfD), wc * 0.8f, hd));
                    // floor: y = y1
                    quads.Add((new Vector3(x0, y1, -halfD), new Vector3(x1, y1, -halfD), new Vector3(x1, y1, halfD), new Vector3(x0, y1, halfD), fc, hd));
                    // ceiling: y = y0 (same as far but different color)
                    quads.Add((new Vector3(x0, y0, -halfD), new Vector3(x1, y0, -halfD), new Vector3(x1, y0, halfD), new Vector3(x0, y0, halfD), cc, hd + 1f));
                }

                quads.Sort((a, b) => b.dist.CompareTo(a.dist));

                foreach (var (a, b, c, d, col, dist) in quads)
                {
                    var p0 = Project(a); var p1 = Project(b); var p2 = Project(c); var p3 = Project(d);
                    if (p0 == null || p1 == null || p2 == null || p3 == null) continue;
                    DrawFillQuad(sb, p0.Value, p1.Value, p2.Value, p3.Value, col);
                }

                // Предметы
                var itemsToDraw = new List<(Item item, float dist, float screenX, float screenY, float w, float h)>();
                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed || item.ParentInventory != null) continue;
                    var delta = item.WorldPosition - new Vector2(CamPos.X, CamPos.Y);
                    float d = delta.Length();
                    if (d > ViewDist * 0.8f || d < 5f) continue;
                    float rx = delta.X * CosYaw;
                    float rz = -delta.X * SinYaw + delta.Y * 0f;
                    // Proper camera-space transform
                    float relX = delta.X, relY = delta.Y, relZ = 0f;
                    float camRx = relX * CosYaw + relZ * SinYaw;
                    float camRz = -relX * SinYaw + relZ * CosYaw;
                    if (camRz < 5f) continue;
                    float sx = ScrCX + (camRx / camRz) * ProjScale;
                    float sy = ScrCY - (relY / camRz) * ProjScale;
                    float w = item.Rect.Width / camRz * ProjScale;
                    float h = item.Rect.Height / camRz * ProjScale;
                    if (w < 3 || h < 3) continue;
                    itemsToDraw.Add((item, d, sx, sy, w, h));
                }
                itemsToDraw.Sort((a, b) => b.dist.CompareTo(a.dist));
                foreach (var (item, dist, sx, sy, w, h) in itemsToDraw)
                {
                    var r = new Rectangle((int)(sx - w / 2), (int)(sy - h / 2), (int)w, (int)h);
                    Color fill = interp == WorldInterp.Xray ? Color.Purple * 0.15f
                               : interp == WorldInterp.WireFrame ? Color.Transparent
                               : interp == WorldInterp.Solid ? Color.SteelBlue * 0.5f
                               : Color.CornflowerBlue * 0.35f;
                    Color border = interp == WorldInterp.WireFrame ? Color.Cyan : Color.White * 0.4f;
                    if (fill.A > 0) sb.Draw(GetTex(), r, fill);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Y, r.Width, 1), border);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Bottom - 1, r.Width, 1), border);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Y, 1, r.Height), border);
                    sb.Draw(GetTex(), new Rectangle(r.Right - 1, r.Y, 1, r.Height), border);
                }
            }
            finally { sb.End(); }

            // Crosshair
            sb.Begin();
            int mcx = ScrW / 2, mcy = ScrH / 2;
            sb.Draw(GetTex(), new Rectangle(mcx - 5, mcy - 1, 10, 2), Color.White * 0.7f);
            sb.Draw(GetTex(), new Rectangle(mcx - 1, mcy - 5, 2, 10), Color.White * 0.7f);
            sb.End();
        }

        private Point? Project(Vector3 world)
        {
            float relX = world.X - CamPos.X;
            float relY = world.Y - CamPos.Y;
            float relZ = world.Z - CamPos.Z;
            float camX = relX * CosYaw + relZ * SinYaw;
            float camZ = -relX * SinYaw + relZ * CosYaw;
            if (camZ < 1f) return null;
            float sx = ScrCX + (camX / camZ) * ProjScale;
            float sy = ScrCY - (relY / camZ) * ProjScale;
            return new Point((int)sx, (int)sy);
        }

        private void DrawFillQuad(SpriteBatch batch, Point a, Point b, Point c, Point d, Color color)
        {
            int minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
            int maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
            int minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
            int maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
            int w = maxX - minX, h = maxY - minY;
            if (w < 1 || h < 1) return;
            if (w > ScrW * 3 || h > ScrH * 3) return;
            batch.Draw(GetTex(), new Rectangle(minX, minY, w, h), color);
        }

        private void ExportCSV()
        {
            try
            {
                Directory.CreateDirectory("barodepth");
                using var w = new StreamWriter("barodepth/objects.csv", append: false);
                w.WriteLine("entityId;prefab;name");
                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed) continue;
                    string p = item.Prefab != null ? item.Prefab.Identifier.Value : "?";
                    w.WriteLine(item.ID + ";" + p + ";" + item.Name);
                }
                Log("CSV saved.", Color.Lime);
            }
            catch (Exception ex) { Log("CSV: " + ex.Message, Color.Red); }
        }

        private void LoadPersist()
        {
            try
            {
                string p = "barodepth/depth3d.settings.xml";
                if (!File.Exists(p)) return;
                var root = XDocument.Load(p).Root;
                if (root == null) return;
                string s = (string)root.Attribute("interp") ?? "Solid";
                if (Enum.TryParse(s, out WorldInterp parsed)) interp = parsed;
                floorLine = (int?)root.Attribute("floor") ?? 0;
            }
            catch { }
        }
        private void Persist()
        {
            try
            {
                Directory.CreateDirectory("barodepth");
                new XDocument(new XElement("s", new XAttribute("interp", interp.ToString()), new XAttribute("floor", floorLine))).Save("barodepth/depth3d.settings.xml");
            }
            catch { }
        }

        private void EnsureSb() { if (sb != null) return; sb = new SpriteBatch(GameMain.Instance.GraphicsDevice); }
        private Texture2D GetTex()
        {
            if (whiteTex != null) return whiteTex;
            whiteTex = new Texture2D(GameMain.Instance.GraphicsDevice, 1, 1);
            whiteTex.SetData(new[] { Color.White });
            return whiteTex;
        }
        private static MethodInfo FindM(Type t, string n, int pc)
        {
            MethodInfo f = null;
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            { if (m.Name != n || m.GetParameters().Length != pc) continue; if (f != null) return null; f = m; }
            return f;
        }
        private void AddHook(string suffix, MethodInfo method, Func<object, Dictionary<string, object>, object> cb)
        {
            string id = Prefix + suffix;
            GameMain.LuaCs.Hook.HookMethod(id, method, (inst, args) => cb(inst, args), LuaCsHook.HookMethodType.After);
            hooks.Add(new HookRegistration { Id = id, Method = method });
        }
        private void RemoveHooks()
        {
            try { GameMain.LuaCs.Hook.Remove(Prefix + "tick", "think"); } catch { }
            try
            {
                object hm = GameMain.LuaCs.Hook;
                foreach (var reg in hooks)
                {
                    foreach (var m in hm.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (m.Name != "UnhookMethod" && m.Name != "RemovePatch") continue;
                        var pars = m.GetParameters();
                        if (pars.Length >= 2 && pars[0].ParameterType == typeof(string))
                        { m.Invoke(hm, pars.Length >= 3 ? new object[] { reg.Id, reg.Method, null } : new object[] { reg.Id, reg.Method }); break; }
                    }
                }
            }
            catch { }
            hooks.Clear();
        }
        private void Fail(string phase, Exception ex) => DebugConsole.ThrowError($"[D3D] {phase}: {ex.Message}", ex);
        private static void Log(string msg, Color color) => DebugConsole.NewMessage("[D3D] " + msg, color);
        private sealed class HookRegistration { public string Id; public MethodInfo Method; }
    }
}
