// ============================================================================
//  Depth3D.cs v3.0 — RAYCASTING 3D (Wolfenstein-style)
//  CSHUB: Modules/Depth3D.cs | F5=3D | F9=режим | LMB=use
//
//  Настоящее 3D через raycasting: из головы игрока пускаются лучи,
//  каждый луч находит стену (границу Hull), рисуется вертикальная полоса.
//  Это даёт настоящее ощущение коридоров как в Wolfenstein 3D.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        private const float MaxRayDist = 2000f;
        private const float RayStep = 8f;         // шаг рейкаста в display units
        private const string Prefix = "d3d.";

        private enum WorldInterp { Solid = 0, WireFrame = 1, Xray = 2, Count = 3 }

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<HookRegistration> hooks = new List<HookRegistration>();
        private WorldInterp interp = WorldInterp.Solid;
        private bool enabled, stopped, ready;
        private bool mouseCaptured, discardNext;
        private bool showHints, clickPending, clickArmed, interactAvail, warnCursor;
        private Character owner;
        private Vector2 savedCursor;
        private float yaw, pitch;
        private Point savedMouse;
        private ButtonState prevLeft;
        private Point clickPos;
        private KeyboardState prevKb;
        private SpriteBatch sb;
        private Texture2D whiteTex;

        public Depth3DMod()
        {
            prevKb = Keyboard.GetState();
            try
            {
                var drawMap = FindM(typeof(GameScreen), "DrawMap", 3);
                var draw = FindM(typeof(GameScreen), "Draw", 3);
                if (drawMap == null || draw == null) throw new MissingMethodException("DrawMap/Draw");

                AddHook("map", drawMap, (inst, args) =>
                {
                    if (stopped || !enabled) return null;
                    if (!HasCtx()) { Disable(false); return null; }
                    try { DoRender(); } catch (Exception ex) { Fail("render", ex); }
                    return null;
                });

                AddHook("hud", draw, (inst, args) =>
                {
                    if (stopped || !enabled || !HasCtx()) return null;
                    try { DoOverlay(); } catch (Exception ex) { Fail("overlay", ex); }
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
                Log("Raycasting 3D ready. F5=on | Mouse=look | LMB=use", Color.Cyan);
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
            bool f9 = kb.IsKeyDown(Keys.F9) && prevKb.IsKeyUp(Keys.F9);
            bool mP = ms.LeftButton == ButtonState.Pressed && prevLeft == ButtonState.Released;
            bool mR = ms.LeftButton == ButtonState.Released && prevLeft == ButtonState.Pressed;
            prevKb = kb; prevLeft = ms.LeftButton;
            if (enabled && !HasCtx()) Disable(false);
            if (!GameMain.WindowActive) { mouseCaptured = false; showHints = false; clickPending = false; clickArmed = false; discardNext = true; return; }
            if (f5 && ready) { if (enabled) Disable(true); else if (!UiOwns()) Enable(); }
            if (!enabled) return;
            if (f9 && !UiOwns()) { interp = (WorldInterp)(((int)interp + 1) % (int)WorldInterp.Count); Log("MODE: " + interp, Color.Cyan); }

            bool ui = UiOwns() || CrewGui();
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
            pitch = MathHelper.Clamp(pitch - dy * MouseSens, -1.0f, 1.0f);
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
            pitch = 0f; enabled = true; discardNext = true;
            Log("ON", Color.LimeGreen);
        }
        private void Disable(bool ann)
        {
            if (!enabled) return;
            enabled = false; clickPending = false; showHints = false; clickArmed = false; mouseCaptured = false; discardNext = true;
            if (owner != null && !owner.Removed && ReferenceEquals(owner, Character.Controlled)) owner.CursorPosition = savedCursor;
            owner = null;
            if (GameMain.WindowActive) Mouse.SetPosition(savedMouse.X, savedMouse.Y);
            if (ann) Log("OFF.", Color.Orange);
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
                if (d > MaxRayDist || d > bestD) continue;
                float ang = (float)Math.Atan2(delta.Y, delta.X);
                if (Math.Abs(MathHelper.WrapAngle(ang - yaw)) < 0.4f) { bestD = d; best = item; }
            }
            if (best != null && !best.Removed)
            {
                float dd = Vector2.Distance(owner.WorldPosition, best.WorldPosition);
                if (dd <= 300f) { try { best.Use(1.0f / 60.0f, owner); } catch (Exception ex) { DebugConsole.ThrowError("[D3D] " + ex.Message); } }
            }
        }

        // ====================================================================
        //  RAYCASTING RENDER
        // ====================================================================

        private void DoRender()
        {
            if (owner == null) return;
            EnsureSb();
            var gd = GameMain.Instance.GraphicsDevice;
            int scrW = GameMain.GraphicsWidth, scrH = GameMain.GraphicsHeight;

            Vector2 eyePos = owner.WorldPosition;
            float halfFov = MathHelper.ToRadians(78f) / 2f;

            sb.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied);
            try
            {
                // Fill background (sky/abyss)
                sb.Draw(GetTex(), new Rectangle(0, 0, scrW, scrH), Color.Black);

                int stripCount = 160; // fewer strips = faster, blockier but still 3D
                float stripW = (float)scrW / stripCount;
                float halfStripAngle = halfFov;

                Color stripCol;
                for (int i = 0; i < stripCount; i++)
                {
                    // Ray angle relative to facing direction
                    float rayOffset = (i / (float)stripCount - 0.5f) * 2f; // -1 to 1
                    float rayAngle = yaw + rayOffset * halfFov;
                    float rayDirX = (float)Math.Cos(rayAngle);
                    float rayDirY = (float)Math.Sin(rayAngle);

                    // Raycast: step forward until we leave the current hull
                    float dist = 0f;
                    bool hitWall = false;
                    Vector2 lastInside = eyePos;
                    Hull startHull = Hull.FindHull(eyePos, null);

                    for (float step = 0f; step < MaxRayDist; step += RayStep)
                    {
                        var testPos = eyePos + new Vector2(rayDirX * step, rayDirY * step);
                        var hull = Hull.FindHull(testPos, startHull);

                        if (hull == null)
                        {
                            // Left all hulls = hit a wall
                            dist = step;
                            hitWall = true;
                            break;
                        }
                        lastInside = testPos;
                        dist = step;
                    }

                    if (!hitWall && dist >= MaxRayDist) dist = MaxRayDist;
                    if (dist < 10f) dist = 10f;

                    // Perspective-corrected distance (fisheye correction)
                    float correctedDist = dist * (float)Math.Cos(rayOffset * halfFov);
                    if (correctedDist < 10f) correctedDist = 10f;

                    // Wall strip height (taller = closer)
                    float wallHeight = 300f; // world height of wall
                    float stripH = wallHeight / correctedDist * (scrH / 2f);
                    if (stripH > scrH * 2f) stripH = scrH * 2f;
                    if (stripH < 2f) stripH = 2f;

                    // Strip center on screen (pitch shifts up/down)
                    float centerY = scrH / 2f + pitch * 200f;
                    float stripY = centerY - stripH / 2f;

                    // Color based on distance (fog) and side
                    float brightness = 1.0f - Math.Min(dist / MaxRayDist, 1.0f);
                    brightness = 0.1f + brightness * 0.9f;

                    // Determine if this is a side wall (N/S) or front wall (E/W)
                    // Simple: check if the last inside point is near a horizontal or vertical edge
                    var hullAtHit = Hull.FindHull(lastInside, null);
                    bool isSideWall = false;
                    if (hullAtHit != null)
                    {
                        var wr = hullAtHit.WorldRect;
                        float distToLeft = Math.Abs(lastInside.X - wr.X);
                        float distToRight = Math.Abs(lastInside.X - (wr.X + wr.Width));
                        float distToTop = Math.Abs(lastInside.Y - wr.Y);
                        float distToBottom = Math.Abs(lastInside.Y - (wr.Y + wr.Height));
                        float minH = Math.Min(distToLeft, distToRight);
                        float minV = Math.Min(distToTop, distToBottom);
                        isSideWall = minH < minV;
                    }

                    if (interp == WorldInterp.Xray)
                    {
                        stripCol = new Color(0.5f * brightness, 0.0f, 0.5f * brightness, 0.3f);
                    }
                    else if (isSideWall)
                    {
                        // Side walls (N/S facing) are darker
                        stripCol = new Color(0.3f * brightness, 0.3f * brightness, 0.35f * brightness);
                    }
                    else
                    {
                        // Front walls (E/W facing) are lighter
                        stripCol = new Color(0.5f * brightness, 0.5f * brightness, 0.55f * brightness);
                    }

                    if (interp == WorldInterp.WireFrame)
                    {
                        // Only draw edges of strips
                        stripCol = new Color(0.0f, stripCol.G, stripCol.B, 0.8f);
                        // Just draw the border lines of the strip
                        int sx = (int)(i * stripW);
                        sb.Draw(GetTex(), new Rectangle(sx, (int)stripY, 1, (int)stripH), stripCol);
                        sb.Draw(GetTex(), new Rectangle(sx, (int)stripY, (int)stripW + 1, 1), stripCol);
                        sb.Draw(GetTex(), new Rectangle(sx, (int)(stripY + stripH - 1), (int)stripW + 1, 1), stripCol);
                    }
                    else
                    {
                        int sx = (int)(i * stripW);
                        int sw = (int)stripW + 1; // +1 to avoid seams
                        sb.Draw(GetTex(), new Rectangle(sx, (int)stripY, sw, (int)stripH), stripCol);
                    }

                    // Draw floor below the wall
                    float floorY = centerY + stripH / 2f;
                    if (floorY < scrH && interp != WorldInterp.Xray)
                    {
                        Color floorCol = new Color(0.2f * brightness, 0.15f * brightness, 0.1f * brightness);
                        sb.Draw(GetTex(), new Rectangle((int)(i * stripW), (int)floorY, (int)stripW + 1, scrH - (int)floorY), floorCol);
                    }

                    // Draw ceiling above the wall
                    float ceilY = centerY - stripH / 2f;
                    if (ceilY > 0 && interp != WorldInterp.Xray)
                    {
                        Color ceilCol = new Color(0.15f * brightness, 0.15f * brightness, 0.18f * brightness);
                        sb.Draw(GetTex(), new Rectangle((int)(i * stripW), 0, (int)stripW + 1, (int)ceilY), ceilCol);
                    }
                }

                // Draw items as sprites on top (nearby only)
                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed || item.ParentInventory != null) continue;
                    if (item.Rect.Width < 10 || item.Rect.Height < 10) continue;
                    var delta = item.WorldPosition - eyePos;
                    float d = delta.Length();
                    if (d > MaxRayDist * 0.5f || d < 10f) continue;
                    float angle = (float)Math.Atan2(delta.Y, delta.X);
                    float relAngle = MathHelper.WrapAngle(angle - yaw);
                    if (Math.Abs(relAngle) > halfFov) continue;

                    // Screen position
                    float screenX = scrW / 2f + (relAngle / halfFov) * (scrW / 2f);
                    float correctedD = d * (float)Math.Cos(relAngle);
                    float itemScale = (scrH / 2f) * (50f / Math.Max(correctedD, 30f));
                    var center = new Vector2(screenX, scrH / 2f + pitch * 100f);
                    int w = (int)(item.Rect.Width * itemScale / 50f);
                    int h = (int)(item.Rect.Height * itemScale / 50f);
                    if (w < 4 || h < 4 || w > scrW || h > scrH) continue;

                    var r = new Rectangle((int)(center.X - w / 2), (int)(center.Y - h / 2), w, h);
                    Color itemCol = interp == WorldInterp.Xray ? Color.Purple * 0.3f : Color.CornflowerBlue * 0.5f;
                    sb.Draw(GetTex(), r, itemCol);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Y, r.Width, 1), Color.White * 0.3f);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Bottom - 1, r.Width, 1), Color.White * 0.3f);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Y, 1, r.Height), Color.White * 0.3f);
                    sb.Draw(GetTex(), new Rectangle(r.Right - 1, r.Y, 1, r.Height), Color.White * 0.3f);
                }
            }
            finally { sb.End(); }

            // Crosshair
            sb.Begin();
            int hc = GameMain.GraphicsWidth / 2, hc2 = GameMain.GraphicsHeight / 2;
            sb.Draw(GetTex(), new Rectangle(hc - 5, hc2 - 1, 10, 2), Color.White * 0.7f);
            sb.Draw(GetTex(), new Rectangle(hc - 1, hc2 - 5, 2, 10), Color.White * 0.7f);
            var f = GUIStyle.Font;
            if (f != null)
            {
                string t = interp.ToString();
                f.DrawString(sb, t, new Vector2(10, 10), Color.Cyan);
            }
            sb.End();
        }

        private void DoOverlay() { /* handled in DoRender */ }

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

        private void EnsureSb()
        {
            if (sb != null) return;
            sb = new SpriteBatch(GameMain.Instance.GraphicsDevice);
        }

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
