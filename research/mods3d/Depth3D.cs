// ============================================================================
//  Depth3D.cs v1.2 — 3D-камера от первого лица с переключаемыми режимами
//
//  CSHUB: Modules/Depth3D.cs
//  F5=3D | F9=режим | F6=стиль | F7=XML | F8=export | B=пол | LMB=use
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
        private const float MouseSensitivity = 0.0027f;
        private const float ViewDistance = 2000f;
        private const string Prefix = "depth3d.v01.";
        private const string ConfigPath = "BaroDepth.xml";

        public enum WorldInterp { BoxWorld = 0, WireFrame = 1, SlicedLayers = 2, Xray = 3, Count = 4 }
        private enum ViewStyle { Normal, Full, Xray }

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<HookRegistration> hooks = new List<HookRegistration>();
        private WorldInterp interp = WorldInterp.BoxWorld;
        private ViewStyle style = ViewStyle.Full;
        private bool enabled, stopped, ready;
        private bool mouseCaptured, discardNextMouseDelta;
        private bool showHints, clickPending, worldClickArmed, interactionHookAvailable, warnedAboutCursor, warmupQueued = true;
        private Character owner;
        private Vector2 savedCursor;
        private float yaw, pitch;
        private Point savedMouse;
        private ButtonState previousLeft;
        private Point clickPosition;
        private KeyboardState previousKeyboard;
        private int floorLine;
        private SpriteBatch sb;
        private Texture2D whiteTex;

        private static string InterpName(WorldInterp w) => w switch
        {
            WorldInterp.BoxWorld => "BOX WORLD",
            WorldInterp.WireFrame => "WIREFRAME",
            WorldInterp.SlicedLayers => "SLICED LAYERS",
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
            LoadPersist();
            previousKeyboard = Keyboard.GetState();

            try
            {
                MethodInfo drawMap = FindMethod(typeof(GameScreen), "DrawMap", 3);
                MethodInfo draw = FindMethod(typeof(GameScreen), "Draw", 3);
                if (drawMap == null || draw == null)
                    throw new MissingMethodException("GameScreen.DrawMap/Draw not found.");

                AddHook("map", drawMap, (inst, args) =>
                {
                    if (stopped || !enabled) return null;
                    if (!HasContext()) { Disable(false); return null; }
                    try { DoRender(); } catch (Exception ex) { Fail("render", ex); }
                    return null;
                });

                AddHook("hud", draw, (inst, args) =>
                {
                    if (stopped || !enabled || !HasContext()) return null;
                    try { DoOverlay(); } catch (Exception ex) { Fail("overlay", ex); }
                    return null;
                });

                MethodInfo cursorM = FindMethod(typeof(Character), "UpdateLocalCursor", 1);
                if (cursorM != null)
                {
                    AddHook("cursor", cursorM, (inst, args) =>
                    {
                        if (stopped || !enabled || !ReferenceEquals(inst, owner)) return null;
                        try { UpdateAim(); } catch (Exception ex) { Fail("aim", ex); }
                        return null;
                    });
                }
                else warnedAboutCursor = true;

                MethodInfo controlM = FindMethod(typeof(Character), "ControlLocalPlayer", 3);
                if (controlM != null)
                {
                    AddHook("interact", controlM, (inst, args) =>
                    {
                        if (stopped || !enabled || !ReferenceEquals(inst, owner)) return null;
                        try { CommitInteract(); } catch (Exception ex) { Fail("interact", ex); }
                        return null;
                    });
                    interactionHookAvailable = true;
                }

                GameMain.LuaCs.Hook.Add("think", Prefix + "update", (args) =>
                {
                    if (stopped) return null;
                    try { Tick(); } catch (Exception ex) { Fail("tick", ex); }
                    return null;
                });

                ready = true;
                Log($"Ready. F5=3D | F9={InterpName(interp)}", Color.Cyan);
            }
            catch (Exception ex)
            {
                stopped = true;
                RemoveHooks();
                Log("Init fail: " + ex.Message, Color.OrangeRed);
            }
        }

        public override void Stop() { Disable(false); RemoveHooks(); }
        public override void Dispose() { Disable(false); RemoveHooks(); }

        private bool HasContext()
            => Screen.Selected is GameScreen && Character.Controlled is { IsDead: false, Removed: false };

        private bool UiOwns()
            => GUI.MouseOn is GUIComponent || GUI.KeyboardDispatcher?.Subscriber != null;

        private bool OnUi()
            => GUI.MouseOn is GUIComponent;

        private bool ItemGuiOpen()
            => Character.Controlled?.SelectedItem != null;

        private void Tick()
        {
            var kb = Keyboard.GetState();
            var ms = Mouse.GetState();

            bool f5 = kb.IsKeyDown(Keys.F5) && previousKeyboard.IsKeyUp(Keys.F5);
            bool f6 = kb.IsKeyDown(Keys.F6) && previousKeyboard.IsKeyUp(Keys.F6);
            bool f7 = kb.IsKeyDown(Keys.F7) && previousKeyboard.IsKeyUp(Keys.F7);
            bool f8 = kb.IsKeyDown(Keys.F8) && previousKeyboard.IsKeyUp(Keys.F8);
            bool f9 = kb.IsKeyDown(Keys.F9) && previousKeyboard.IsKeyUp(Keys.F9);
            bool b = PlayerInput.KeyHit(Keys.B);
            bool mPress = ms.LeftButton == ButtonState.Pressed && previousLeft == ButtonState.Released;
            bool mRelease = ms.LeftButton == ButtonState.Released && previousLeft == ButtonState.Pressed;
            previousKeyboard = kb; previousLeft = ms.LeftButton;

            if (enabled && !HasContext()) Disable(false);
            if (!GameMain.WindowActive)
            {
                mouseCaptured = false; showHints = false;
                clickPending = false; worldClickArmed = false;
                discardNextMouseDelta = true; return;
            }

            if (f5 && ready)
            {
                if (enabled) Disable(true);
                else if (!UiOwns()) Enable();
            }
            if (!enabled) return;

            if (f9 && !UiOwns())
            {
                interp = (WorldInterp)(((int)interp + 1) % (int)WorldInterp.Count);
                Persist();
                Log("MODE: " + InterpName(interp), Color.Cyan);
            }

            if (f7 && !UiOwns()) Log("XML reload: not implemented in v1.2.", Color.Orange);
            if (f8 && !UiOwns()) ExportCSV();
            if (b) { floorLine = (floorLine + 1) % 3; Persist(); }

            bool ui = UiOwns() || ItemGuiOpen();
            if (f6 && !UiOwns())
            {
                style = (ViewStyle)(((int)style + 1) % 3);
                Log("STYLE: " + StyleName(style), Color.Cyan);
            }
            showHints = kb.IsKeyDown(Keys.LeftAlt) && !ui;
            bool capture = !ui && !showHints;
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
            var ptr = capture ? new Point(cx, cy) : new Point(ms.X, ms.Y);
            bool worldIn = !ui && !OnUi() && (showHints || !PlayerInput.KeyDown(InputType.Aim));
            if (!worldIn) { worldClickArmed = false; clickPending = false; }
            if (mPress && worldIn && interactionHookAvailable)
                worldClickArmed = TryPick(ptr, out _, out _);
            if (mRelease && worldClickArmed && worldIn)
            { clickPosition = ptr; clickPending = true; worldClickArmed = false; }

            if (!capture)
            {
                mouseCaptured = false; discardNextMouseDelta = true;
                if (warnedAboutCursor && !ui) UpdateAim();
                return;
            }
            if (!mouseCaptured || discardNextMouseDelta)
            {
                mouseCaptured = true; discardNextMouseDelta = false;
                Mouse.SetPosition(cx, cy);
                if (warnedAboutCursor) UpdateAim();
                return;
            }
            float dx = MathHelper.Clamp(ms.X - cx, -250f, 250f);
            float dy = MathHelper.Clamp(ms.Y - cy, -250f, 250f);
            yaw = MathHelper.WrapAngle(yaw + dx * MouseSensitivity);
            pitch = MathHelper.Clamp(pitch - dy * MouseSensitivity, -1.38f, 1.38f);
            Mouse.SetPosition(cx, cy);
            if (warnedAboutCursor) UpdateAim();
        }

        private void Enable()
        {
            var ch = Character.Controlled;
            if (!(Screen.Selected is GameScreen) || ch == null || ch.IsDead || ch.Removed)
            { Log("Need living character in round.", Color.Orange); return; }
            owner = ch;
            savedCursor = ch.CursorPosition;
            var ms = Mouse.GetState();
            savedMouse = new Point(ms.X, ms.Y);
            previousLeft = ms.LeftButton;
            clickPending = false; showHints = false; worldClickArmed = false;
            yaw = (ch.AnimController?.Dir ?? 1f) >= 0f ? 0f : MathHelper.Pi;
            pitch = 0f; enabled = true; warmupQueued = true; discardNextMouseDelta = true;
            Log("ON / " + InterpName(interp), Color.LimeGreen);
        }

        private void Disable(bool announce)
        {
            if (!enabled) return;
            enabled = false;
            clickPending = false; showHints = false; worldClickArmed = false;
            mouseCaptured = false; discardNextMouseDelta = true;
            if (owner != null && !owner.Removed && ReferenceEquals(owner, Character.Controlled))
                owner.CursorPosition = savedCursor;
            owner = null;
            if (GameMain.WindowActive) Mouse.SetPosition(savedMouse.X, savedMouse.Y);
            if (announce) Log("OFF / 2D view.", Color.Orange);
        }

        private void UpdateAim()
        {
            if (owner == null || owner.Removed) return;
            var dir = new Vector2((float)Math.Cos(yaw), (float)Math.Sin(yaw));
            owner.CursorPosition = owner.WorldPosition + dir * 500f;
        }

        private void CommitInteract()
        {
            if (!clickPending || owner == null || owner.Removed) return;
            clickPending = false;
            if (!TryPick(clickPosition, out Item target, out _)) return;
            if (target == null || target.Removed) return;
            float dist = Vector2.Distance(owner.WorldPosition, target.WorldPosition);
            if (dist > 300f) return;
            try { target.Use(1.0f / 60.0f, owner); }
            catch (Exception ex) { DebugConsole.ThrowError("[D3D] Use: " + ex.Message); }
        }

        private bool TryPick(Point pixel, out Item target, out Vector2 worldPos)
        {
            target = null; worldPos = Vector2.Zero;
            var ch = Character.Controlled;
            if (ch == null) return false;
            float best = float.MaxValue;
            foreach (var item in Item.ItemList)
            {
                if (item == null || item.Removed || item.ParentInventory != null) continue;
                var delta = item.WorldPosition - ch.WorldPosition;
                float d = delta.Length();
                if (d > ViewDistance || d > best) continue;
                float angle = (float)Math.Atan2(delta.Y, delta.X);
                if (Math.Abs(MathHelper.WrapAngle(angle - yaw)) < 0.35f)
                { best = d; target = item; worldPos = item.WorldPosition; }
            }
            return target != null;
        }

        private void DoRender()
        {
            if (owner == null) return;
            EnsureSb();

            sb.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied);
            try
            {
                var origin = owner.WorldPosition;
                float cos = (float)Math.Cos(yaw), sin = (float)Math.Sin(yaw);
                var items = new List<(Item item, float dist, float lat)>();

                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed || item.ParentInventory != null) continue;
                    if (item.GetComponent<Holdable>() is { Attached: false }) continue;
                    var delta = item.WorldPosition - origin;
                    float fwd = delta.X * cos + delta.Y * sin;
                    if (fwd < 10f || fwd > ViewDistance) continue;
                    float lat = -delta.X * sin + delta.Y * cos;
                    items.Add((item, fwd, lat));
                }

                items.Sort((a, b) => b.dist.CompareTo(a.dist));
                int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
                bool wire = interp == WorldInterp.WireFrame;
                bool xray = interp == WorldInterp.Xray;
                bool slice = interp == WorldInterp.SlicedLayers;

                foreach (var (item, dist, lat) in items)
                {
                    var rect = item.Rect;
                    if (rect.Width <= 0 || rect.Height <= 0) continue;
                    float sc = 300f / Math.Max(dist, 50f);
                    int w = (int)(rect.Width * sc);
                    int h = (int)(rect.Height * sc);
                    if (w < 2 || h < 2) continue;
                    var pos = new Vector2(cx + lat * sc, cy);
                    var r = new Rectangle((int)pos.X - w / 2, (int)pos.Y - h / 2, w, h);

                    Color fill = slice ? Color.Orange * 0.35f : Color.CornflowerBlue * 0.3f;
                    Color border = Color.White * 0.4f;
                    if (xray) { fill = Color.Purple * 0.25f; border = Color.Magenta * 0.5f; }
                    if (wire) fill = Color.Transparent;

                    if (fill.A > 0) sb.Draw(GetTex(), r, fill);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Y, r.Width, 1), border);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Bottom - 1, r.Width, 1), border);
                    sb.Draw(GetTex(), new Rectangle(r.X, r.Y, 1, r.Height), border);
                    sb.Draw(GetTex(), new Rectangle(r.Right - 1, r.Y, 1, r.Height), border);
                }

                Color fc = floorLine == 1 ? Color.White * 0.15f
                         : floorLine == 2 ? Color.White * 0.06f
                         : Color.White * 0.3f;
                int fy = cy + (int)(140f * 300f / 200f);
                if (fy > 0 && fy < GameMain.GraphicsHeight)
                    sb.Draw(GetTex(), new Rectangle(0, fy, GameMain.GraphicsWidth, 1), fc);
            }
            finally { sb.End(); }
        }

        private void DoOverlay()
        {
            EnsureSb();
            sb.Begin();
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
            sb.Draw(GetTex(), new Rectangle(cx - 5, cy - 1, 10, 2), Color.White * 0.7f);
            sb.Draw(GetTex(), new Rectangle(cx - 1, cy - 5, 2, 10), Color.White * 0.7f);

            var font = GUIStyle.Font;
            if (font != null)
            {
                string txt = $"[{InterpName(interp)}] {StyleName(style)}";
                var sz = font.MeasureString(txt);
                var pos = new Vector2(GameMain.GraphicsWidth - sz.X - 20f, 20f);
                font.DrawString(sb, txt, pos + Vector2.One, Color.Black, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
                font.DrawString(sb, txt, pos, Color.Cyan, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0f);
            }
            sb.End();
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
                    w.WriteLine($"{item.ID};{p};{item.Name}");
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
                string s = (string)root.Attribute("interp") ?? "BoxWorld";
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
                string p = "barodepth/depth3d.settings.xml";
                new XDocument(new XElement("s",
                    new XAttribute("interp", interp.ToString()),
                    new XAttribute("floor", floorLine))).Save(p);
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                XDocument.Load(ConfigPath);
                Log("XML loaded.", Color.Lime);
            }
            catch (Exception ex) { Log("XML: " + ex.Message, Color.OrangeRed); }
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

        private static MethodInfo FindMethod(Type t, string name, int paramCount)
        {
            MethodInfo found = null;
            foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != name || m.GetParameters().Length != paramCount) continue;
                if (found != null) return null;
                found = m;
            }
            return found;
        }

        private void AddHook(string suffix, MethodInfo method, Func<object, Dictionary<string, object>, object> cb)
        {
            string id = Prefix + suffix;
            GameMain.LuaCs.Hook.HookMethod(id, method,
                (inst, args) => cb(inst, args), LuaCsHook.HookMethodType.After);
            hooks.Add(new HookRegistration { Id = id, Method = method });
        }

        private void RemoveHooks()
        {
            try { GameMain.LuaCs.Hook.Remove(Prefix + "update", "think"); } catch { }
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
                        {
                            m.Invoke(hm, pars.Length >= 3
                                ? new object[] { reg.Id, reg.Method, null }
                                : new object[] { reg.Id, reg.Method });
                            break;
                        }
                    }
                }
            }
            catch { }
            hooks.Clear();
        }

        private void Fail(string phase, Exception ex)
            => DebugConsole.ThrowError($"[D3D] {phase}: {ex.Message}", ex);

        private static void Log(string msg, Color color)
            => DebugConsole.NewMessage("[D3D] " + msg, color);

        private sealed class HookRegistration { public string Id; public MethodInfo Method; }
    }
}
