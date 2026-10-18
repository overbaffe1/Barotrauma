// ============================================================================
//  Depth3D.cs — Switcher для интерпретации 2D-мира Barotrauma в 3D (v1.1)
//
//  ОДИН .cs файл для CSHUB. Кидается в папку Modules/ — работает.
//
//  КЛАВИШИ:
//    F5  — вкл/выкл 3D-камеру
//    F9  — СЛЕДУЮЩИЙ РЕЖИМ ИНТЕРПРЕТАЦИИ МИРА (4 режима)
//    F6  — стиль рендера (Normal / Full / Xray)
//    F7  — перезагрузить BaroDepth.xml
//    F8  — выгрузить IDs в CSV
//    B   — стиль линии пола
//    LMB — взаимодействие
//
//  РЕЖИМЫ (F9):
//    BoxWorld      — предметы = полупрозрачные боксы
//    WireFrame     — каркас боксов (видно сквозь)
//    SlicedLayers  — оранжевые боксы (лучше виден параллакс)
//    Xray          — всё фиолетовое и сквозное
//
//  ТРЕБОВАНИЯ: CSHUB (LuaCs). Клиент-only, сервер не нужен.
//  Не грузи вместе с другими camera модами.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        // ====================================================================
        //  КОНСТАНТЫ
        // ====================================================================

        private const float MouseSensitivity = 0.0027f;
        private const float FieldOfViewDegrees = 78f;
        private const float ViewDistance = 2000f;
        private const string Prefix = "depth3d.v01.";
        private const string ConfigPath = "BaroDepth.xml";

        // ====================================================================
        //  ENUMS
        // ====================================================================

        public enum WorldInterpretation { BoxWorld = 0, WireFrame = 1, SlicedLayers = 2, Xray = 3, Count = 4 }
        private enum RenderMode { Boxes, Contours, Slices }
        private enum ViewMode { Normal, Full, Xray }

        private static RenderMode ToRenderMode(WorldInterpretation w) => w switch
        {
            WorldInterpretation.WireFrame => RenderMode.Contours,
            WorldInterpretation.SlicedLayers => RenderMode.Slices,
            _ => RenderMode.Boxes
        };

        private static ViewMode ToViewMode(WorldInterpretation w) => w switch
        {
            WorldInterpretation.Xray => ViewMode.Xray,
            _ => ViewMode.Full
        };

        private static string InterpName(WorldInterpretation w) => w switch
        {
            WorldInterpretation.BoxWorld => "BOX WORLD",
            WorldInterpretation.WireFrame => "WIREFRAME",
            WorldInterpretation.SlicedLayers => "SLICED LAYERS",
            WorldInterpretation.Xray => "XRAY",
            _ => "?"
        };

        // ====================================================================
        //  ПОЛЯ
        // ====================================================================

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<HookRegistration> hooks = new List<HookRegistration>();

        private WorldInterpretation interpretation = WorldInterpretation.BoxWorld;
        private ViewMode viewMode = ViewMode.Full;
        private Renderer renderer;
        private bool enabled;
        private bool stopped;
        private bool ready;

        private bool mouseCaptured;
        private bool discardNextMouseDelta;
        private Point savedMouse;
        private ButtonState previousLeft;

        private Character owner;
        private Vector2 savedCursor;
        private float yaw;
        private float pitch;

        private KeyboardState previousKeyboard;
        private bool showHints;
        private bool clickPending;
        private bool worldClickArmed;
        private Point clickPosition;
        private bool interactionHookAvailable;
        private bool warnedAboutCursor;
        private bool warmupQueued = true;

        private int floorLine;
        private Settings settings = new Settings();
        private SpriteBatch spriteBatch;

        // ====================================================================
        //  ВНУТРЕННИЕ КЛАССЫ
        // ====================================================================

        private sealed class HookRegistration { public string Id; public MethodInfo Method; }

        private sealed class ObjectRule
        {
            public string Prefab, Submarine, Side;
            public int? EntityId, EditorId;
            public float? Offset, Depth, Scale;
        }

        private sealed class Settings
        {
            public float DepthScale = 1.8f;
            public float RoomHalfDepth = 155f;
            public float LayerSpread = 17f;
            public float SurfaceInset = 28f;
            public bool ShowIds = true;
            public bool WarmupAll = true;
            public bool SurfaceTexture = true;
            public List<ObjectRule> Objects = new List<ObjectRule>();
        }

        private static class SettingsStore
        {
            private const string StorePath = "barodepth/depth3d.settings.xml";
            public static string Interpretation = "BoxWorld";
            public static int FloorLine;

            public static void Load()
            {
                try
                {
                    if (!File.Exists(StorePath)) return;
                    var root = XDocument.Load(StorePath).Root;
                    if (root == null) return;
                    Interpretation = (string)root.Attribute("interpretation") ?? "BoxWorld";
                    FloorLine = (int?)root.Attribute("floorLine") ?? 0;
                }
                catch { }
            }

            public static void Save(string interp, int floor)
            {
                try
                {
                    Directory.CreateDirectory("barodepth");
                    new XDocument(new XElement("settings",
                        new XAttribute("interpretation", interp),
                        new XAttribute("floorLine", floor))).Save(StorePath);
                }
                catch { }
            }
        }

        private static class PixelTex
        {
            private static Texture2D tex;
            public static Texture2D Get
            {
                get
                {
                    if (tex != null) return tex;
                    tex = new Texture2D(GameMain.Instance.GraphicsDevice, 1, 1);
                    tex.SetData(new[] { Color.White });
                    return tex;
                }
            }
        }

        private sealed class NativeInteraction
        {
            public void Commit(Item target, Character character)
            {
                if (target == null || character == null || target.Removed) return;
                float dist = Vector2.Distance(character.WorldPosition, target.WorldPosition);
                if (dist > 300f) return;

                // Универсальное: item.Use() — игра сама разрулит дверь/пикап/контроллер
                try { target.Use(1.0f / 60.0f, character); }
                catch (Exception ex) { DebugConsole.ThrowError("[Depth3D] Use: " + ex.Message); }
            }
        }

        private readonly NativeInteraction interaction = new NativeInteraction();

        // ====================================================================
        //  КОНСТРУКТОР
        // ====================================================================

        public Depth3DMod()
        {
            SettingsStore.Load();
            if (Enum.TryParse(SettingsStore.Interpretation, out WorldInterpretation saved))
                interpretation = saved;
            floorLine = SettingsStore.FloorLine;
            previousKeyboard = Keyboard.GetState();

            try
            {
                MethodInfo drawMap = FindMethod(typeof(GameScreen), "DrawMap", 3);
                MethodInfo draw = FindMethod(typeof(GameScreen), "Draw", 3);
                if (drawMap == null || draw == null)
                    throw new MissingMethodException("GameScreen.DrawMap/Draw not found.");

                AddAfter("map", drawMap, (instance, args) =>
                {
                    if (stopped || !enabled) return null;
                    if (!HasContext()) { Disable(false); return null; }
                    try
                    {
                        renderer ??= new Renderer(settings, ViewDistance);
                        if (warmupQueued) { renderer.WarmupPending = true; warmupQueued = false; }
                        renderer.Render(owner, ToRenderMode(interpretation), ToViewMode(interpretation),
                            yaw, pitch, FieldOfViewDegrees, clock.Elapsed.TotalSeconds);
                    }
                    catch (Exception ex) { Fail("render", ex); }
                    return null;
                });

                AddAfter("hud", draw, (instance, args) =>
                {
                    if (stopped || !enabled || renderer == null || !HasContext()) return null;
                    try
                    {
                        renderer.DrawOverlay(ToRenderMode(interpretation), mouseCaptured, owner.InWater, showHints);
                        DrawHud();
                    }
                    catch (Exception ex) { Fail("overlay", ex); }
                    return null;
                });

                MethodInfo cursorUpdate = FindMethod(typeof(Character), "UpdateLocalCursor", 1);
                if (cursorUpdate != null)
                {
                    AddAfter("cursor", cursorUpdate, (instance, args) =>
                    {
                        if (stopped || !enabled || !ReferenceEquals(instance, owner)) return null;
                        try { UpdateAim(); } catch (Exception ex) { Fail("aim", ex); }
                        return null;
                    });
                }
                else warnedAboutCursor = true;

                MethodInfo localControl = FindMethod(typeof(Character), "ControlLocalPlayer", 3);
                if (localControl != null)
                {
                    AddAfter("interact", localControl, (instance, args) =>
                    {
                        if (stopped || !enabled || !ReferenceEquals(instance, owner)) return null;
                        try { CommitInteraction(); } catch (Exception ex) { Fail("interact", ex); }
                        return null;
                    });
                    interactionHookAvailable = true;
                }
                else Log("Interaction hook unavailable.", Color.Orange);

                GameMain.LuaCs.Hook.Add("think", Prefix + "update", (args) =>
                {
                    if (stopped) return null;
                    try { Update(); } catch (Exception ex) { Fail("update", ex); }
                    return null;
                });

                ready = true;
                Log($"Ready. F5:3D | F9:{InterpName(interpretation)} | F6:style | LMB:use", Color.Cyan);
            }
            catch (Exception ex)
            {
                stopped = true;
                RemoveHooks();
                Log("Failed: " + ex.Message, Color.OrangeRed);
            }
        }

        // ====================================================================
        //  ACsMod ОБЯЗАТЕЛЬНЫЕ МЕТОДЫ
        // ====================================================================

        public override void Stop()
        {
            Disable(false);
            RemoveHooks();
            base.Dispose();
        }

        public override void Dispose()
        {
            Disable(false);
            RemoveHooks();
            base.Dispose();
        }

        // ====================================================================
        //  КОНТЕКСТ
        // ====================================================================

        private bool HasContext()
            => Screen.Selected is GameScreen && Character.Controlled is { IsDead: false, Removed: false };

        private bool UiOwnsInput()
            => GUI.MouseOn is GUIComponent || GUI.KeyboardDispatcher?.Subscriber != null;

        private bool PointerOnUi()
            => GUI.MouseOn is GUIComponent;

        private void EnsureSpriteBatch()
        {
            if (spriteBatch != null) return;
            spriteBatch = new SpriteBatch(GameMain.Instance.GraphicsDevice);
        }

        // ====================================================================
        //  ГЛАВНЫЙ ЦИКЛ
        // ====================================================================

        private void Update()
        {
            KeyboardState keyboard = Keyboard.GetState();
            MouseState mouse = Mouse.GetState();

            bool toggle = keyboard.IsKeyDown(Keys.F5) && previousKeyboard.IsKeyUp(Keys.F5);
            bool toggleStyle = keyboard.IsKeyDown(Keys.F6) && previousKeyboard.IsKeyUp(Keys.F6);
            bool reload = keyboard.IsKeyDown(Keys.F7) && previousKeyboard.IsKeyUp(Keys.F7);
            bool export = keyboard.IsKeyDown(Keys.F8) && previousKeyboard.IsKeyUp(Keys.F8);
            bool nextMode = keyboard.IsKeyDown(Keys.F9) && previousKeyboard.IsKeyUp(Keys.F9);
            bool pressed = mouse.LeftButton == ButtonState.Pressed && previousLeft == ButtonState.Released;
            bool released = mouse.LeftButton == ButtonState.Released && previousLeft == ButtonState.Pressed;
            previousKeyboard = keyboard; previousLeft = mouse.LeftButton;

            if (enabled && !HasContext()) Disable(false);
            if (!GameMain.WindowActive)
            {
                mouseCaptured = false; showHints = false; clickPending = false; worldClickArmed = false;
                discardNextMouseDelta = true; return;
            }

            if (toggle && ready)
            {
                if (enabled) Disable(true);
                else if (!UiOwnsInput()) Enable();
            }
            if (!enabled) return;

            if (nextMode && !UiOwnsInput())
            {
                interpretation = (WorldInterpretation)(((int)interpretation + 1) % (int)WorldInterpretation.Count);
                SettingsStore.Save(interpretation.ToString(), floorLine);
                renderer?.InvalidateView();
                warmupQueued = true;
                Log("РЕЖИМ: " + InterpName(interpretation), Color.Cyan);
            }

            if (reload && !UiOwnsInput()) { LoadSettings(); warmupQueued = true; }
            if (PlayerInput.KeyHit(Keys.B))
            {
                floorLine = (floorLine + 1) % 3;
                SettingsStore.Save(interpretation.ToString(), floorLine);
            }
            if (export && !UiOwnsInput() && renderer != null) renderer.ExportObjects();

            bool ui = UiOwnsInput() || HasItemGui();
            if (toggleStyle && !UiOwnsInput())
            {
                viewMode = (ViewMode)(((int)viewMode + 1) % 3);
                renderer?.InvalidateView();
                warmupQueued = true;
            }
            showHints = keyboard.IsKeyDown(Keys.LeftAlt) && !ui;
            bool capture = !ui && !showHints;
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
            Point pointer = capture ? new Point(cx, cy) : new Point(mouse.X, mouse.Y);
            bool worldInput = !ui && !PointerOnUi() && (showHints || !PlayerInput.KeyDown(InputType.Aim));
            if (!worldInput) { worldClickArmed = false; clickPending = false; }
            if (pressed && worldInput && interactionHookAvailable && renderer != null)
                worldClickArmed = renderer.TryPick(pointer, yaw, pitch, showHints, out _, out _);
            if (released && worldClickArmed && worldInput)
            {
                clickPosition = pointer; clickPending = true; worldClickArmed = false;
            }
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
            float dx = MathHelper.Clamp(mouse.X - cx, -250f, 250f);
            float dy = MathHelper.Clamp(mouse.Y - cy, -250f, 250f);
            yaw = MathHelper.WrapAngle(yaw + dx * MouseSensitivity);
            pitch = MathHelper.Clamp(pitch - dy * MouseSensitivity, -1.38f, 1.38f);
            Mouse.SetPosition(cx, cy);
            if (warnedAboutCursor) UpdateAim();
        }

        // ====================================================================
        //  ВКЛ/ВЫКЛ
        // ====================================================================

        private void Enable()
        {
            Character character = Character.Controlled;
            if (!(Screen.Selected is GameScreen) || character == null || character.IsDead || character.Removed)
            {
                Log("Нужен живой персонаж в раунде.", Color.Orange);
                return;
            }
            owner = character;
            savedCursor = character.CursorPosition;
            MouseState mouse = Mouse.GetState();
            savedMouse = new Point(mouse.X, mouse.Y);
            previousLeft = mouse.LeftButton;
            clickPending = false; showHints = false; worldClickArmed = false;
            yaw = (character.AnimController?.Dir ?? 1f) >= 0f ? 0f : MathHelper.Pi;
            pitch = 0f;
            enabled = true;
            warmupQueued = true;
            discardNextMouseDelta = true;
            Log("ON / " + InterpName(interpretation), Color.LimeGreen);
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
            renderer?.ClearScene();
            if (announce) Log("OFF / 2D view.", Color.Orange);
        }

        // ====================================================================
        //  AIM + INTERACT
        // ====================================================================

        private void UpdateAim()
        {
            if (owner == null || owner.Removed) return;
            var dir = new Vector2((float)Math.Cos(yaw), (float)Math.Sin(yaw));
            owner.CursorPosition = owner.WorldPosition + dir * 500f;
        }

        private void CommitInteraction()
        {
            if (!clickPending) return;
            clickPending = false;
            if (owner == null || owner.Removed) return;
            if (renderer == null) return;
            if (!renderer.TryPick(clickPosition, yaw, pitch, showHints, out Item target, out Vector2 worldPos)) return;
            interaction.Commit(target, owner);
        }

        // ====================================================================
        //  HUD
        // ====================================================================

        private void DrawHud()
        {
            EnsureSpriteBatch();
            if (spriteBatch == null) return;
            var font = GUIStyle.Font;
            if (font == null) return;
            string text = $"[{InterpName(interpretation)}] {viewMode}";
            Vector2 size = font.MeasureString(text);
            var pos = new Vector2(GameMain.GraphicsWidth - size.X - 20f, 20f);
            spriteBatch.Begin();
            spriteBatch.DrawString(font, text, pos + Vector2.One, Color.Black);
            spriteBatch.DrawString(font, text, pos, Color.Cyan);
            spriteBatch.End();
        }

        // ====================================================================
        //  НАСТРОЙКИ
        // ====================================================================

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var doc = XDocument.Load(ConfigPath);
                var global = doc.Root?.Element("Global");
                if (global != null)
                {
                    settings.DepthScale = (float?)global.Attribute("depthScale") ?? settings.DepthScale;
                    settings.RoomHalfDepth = (float?)global.Attribute("roomHalfDepth") ?? settings.RoomHalfDepth;
                    settings.LayerSpread = (float?)global.Attribute("layerSpread") ?? settings.LayerSpread;
                    settings.SurfaceInset = (float?)global.Attribute("surfaceInset") ?? settings.SurfaceInset;
                    settings.ShowIds = (bool?)global.Attribute("showIds") ?? settings.ShowIds;
                    settings.WarmupAll = (bool?)global.Attribute("warmupAll") ?? settings.WarmupAll;
                    settings.SurfaceTexture = (bool?)global.Attribute("surfaceTexture") ?? settings.SurfaceTexture;
                }
                var objects = doc.Root?.Element("Objects");
                settings.Objects.Clear();
                if (objects != null)
                {
                    foreach (var el in objects.Elements("Object"))
                    {
                        settings.Objects.Add(new ObjectRule
                        {
                            Prefab = (string)el.Attribute("prefab"),
                            Submarine = (string)el.Attribute("submarine"),
                            Side = (string)el.Attribute("side") ?? "both",
                            EntityId = (int?)el.Attribute("entityId"),
                            EditorId = (int?)el.Attribute("editorId"),
                            Offset = (float?)el.Attribute("offset"),
                            Depth = (float?)el.Attribute("depth"),
                            Scale = (float?)el.Attribute("scale"),
                        });
                    }
                }
            }
            catch (Exception ex) { Log("Config: " + ex.Message, Color.OrangeRed); }
        }

        // ====================================================================
        //  УТИЛИТЫ
        // ====================================================================

        private static MethodInfo FindMethod(Type type, string name, int parameterCount)
        {
            MethodInfo found = null;
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != name || m.GetParameters().Length != parameterCount) continue;
                if (found != null) return null;
                found = m;
            }
            return found;
        }

        private void AddAfter(string suffix, MethodInfo method, Func<object, Dictionary<string, object>, object> callback)
        {
            string id = Prefix + suffix;
            GameMain.LuaCs.Hook.HookMethod(id, method,
                (instance, arguments) => callback(instance, arguments),
                LuaCsHook.HookMethodType.After);
            hooks.Add(new HookRegistration { Id = id, Method = method });
        }

        private void RemoveHooks()
        {
            // Через рефлексию — чтобы работать на любой версии LuaCs
            try { GameMain.LuaCs.Hook.Remove(Prefix + "update", "think"); } catch { }
            object hookManager = GameMain.LuaCs.Hook;
            foreach (var reg in hooks)
            {
                try
                {
                    foreach (MethodInfo m in hookManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                    {
                        if (m.Name != "UnhookMethod" && m.Name != "RemovePatch") continue;
                        var pars = m.GetParameters();
                        if (pars.Length >= 2 && pars[0].ParameterType == typeof(string))
                        {
                            m.Invoke(hookManager, pars.Length >= 3
                                ? new object[] { reg.Id, reg.Method, null }
                                : new object[] { reg.Id, reg.Method });
                            break;
                        }
                    }
                }
                catch { }
            }
            hooks.Clear();
        }

        private void Fail(string phase, Exception ex)
        {
            DebugConsole.ThrowError($"[Depth3D] {phase}: {ex.Message}", ex);
        }

        private static void Log(string msg, Color color)
        {
            DebugConsole.NewMessage("[Depth3D] " + msg, color);
        }

        // ====================================================================
        //  RENDERER (минимальная версия — цветные боксы)
        //  Для полной графики замени на Renderer из "V3 3D Barotrauma.cs" :954
        // ====================================================================

        private sealed class Renderer : IDisposable
        {
            public bool WarmupPending;
            private readonly Settings settings;
            private readonly float viewDistance;
            private SpriteBatch sb;

            public Renderer(Settings cfg, float viewDist)
            {
                settings = cfg;
                viewDistance = viewDist;
            }

            private void EnsureSb()
            {
                if (sb != null) return;
                sb = new SpriteBatch(GameMain.Instance.GraphicsDevice);
            }

            public void Render(Character player, RenderMode mode, ViewMode sceneView, float yaw, float pitch, float fov, double time)
            {
                if (player == null) return;
                EnsureSb();
                if (sb == null) return;

                sb.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied);
                try
                {
                    var origin = player.WorldPosition;
                    float cos = (float)Math.Cos(yaw), sin = (float)Math.Sin(yaw);
                    var visible = new List<(Item item, float dist, float lateral)>();

                    foreach (var item in Item.ItemList)
                    {
                        if (item == null || item.Removed || item.ParentInventory != null) continue;
                        if (item.GetComponent<Holdable>() is { Attached: false }) continue;
                        var delta = item.WorldPosition - origin;
                        float forward = delta.X * cos + delta.Y * sin;
                        if (forward < 10f || forward > viewDistance) continue;
                        float lateral = -delta.X * sin + delta.Y * cos;
                        visible.Add((item, forward, lateral));
                    }

                    visible.Sort((a, b) => b.dist.CompareTo(a.dist));
                    int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;

                    foreach (var (item, dist, lat) in visible)
                    {
                        var rect = item.Rect;
                        if (rect.Width <= 0 || rect.Height <= 0) continue;
                        float scale = 300f / Math.Max(dist, 50f);
                        int w = (int)(rect.Width * scale);
                        int h = (int)(rect.Height * scale);
                        if (w < 2 || h < 2) continue;
                        var screenPos = new Vector2(cx + lat * scale, cy);
                        Color fill = mode switch
                        {
                            RenderMode.Contours => Color.Transparent,
                            RenderMode.Slices => Color.Orange * 0.35f,
                            _ => Color.CornflowerBlue * 0.3f
                        };
                        Color border = Color.White * 0.4f;
                        if (sceneView == ViewMode.Xray) { fill = Color.Purple * 0.25f; border = Color.Magenta * 0.5f; }

                        var r = new Rectangle((int)screenPos.X - w / 2, (int)screenPos.Y - h / 2, w, h);
                        if (fill.A > 0) sb.Draw(PixelTex.Get, r, fill);
                        sb.Draw(PixelTex.Get, new Rectangle(r.X, r.Y, r.Width, 1), border);
                        sb.Draw(PixelTex.Get, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), border);
                        sb.Draw(PixelTex.Get, new Rectangle(r.X, r.Y, 1, r.Height), border);
                        sb.Draw(PixelTex.Get, new Rectangle(r.Right - 1, r.Y, 1, r.Height), border);
                    }

                    // Пол
                    Color floorCol = floorLine == 1 ? Color.White * 0.15f
                                   : floorLine == 2 ? Color.White * 0.06f
                                   : Color.White * 0.3f;
                    int floorY = cy + (int)(140f * 300f / 200f);
                    if (floorY < GameMain.GraphicsHeight && floorY > 0)
                        sb.Draw(PixelTex.Get, new Rectangle(0, floorY, GameMain.GraphicsWidth, 1), floorCol);
                }
                finally { sb.End(); }
            }

            public void DrawOverlay(RenderMode mode, bool captured, bool inWater, bool hints)
            {
                EnsureSb();
                if (sb == null) return;
                sb.Begin();
                int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
                sb.Draw(PixelTex.Get, new Rectangle(cx - 5, cy - 1, 10, 2), Color.White * 0.7f);
                sb.Draw(PixelTex.Get, new Rectangle(cx - 1, cy - 5, 2, 10), Color.White * 0.7f);
                sb.End();
            }

            public bool TryPick(Point pixel, float yaw, float pitch, bool allowLabels, out Item target, out Vector2 worldPos)
            {
                target = null; worldPos = Vector2.Zero;
                var character = Character.Controlled;
                if (character == null) return false;
                float bestDist = float.MaxValue;
                foreach (var item in Item.ItemList)
                {
                    if (item == null || item.Removed || item.ParentInventory != null) continue;
                    var delta = item.WorldPosition - character.WorldPosition;
                    float dist = delta.Length();
                    if (dist > viewDistance || dist > bestDist) continue;
                    float angle = (float)Math.Atan2(delta.Y, delta.X);
                    float diff = Math.Abs(MathHelper.WrapAngle(angle - yaw));
                    if (diff < 0.35f) { bestDist = dist; target = item; worldPos = item.WorldPosition; }
                }
                return target != null;
            }

            public void InvalidateView() { }
            public void ClearScene() { }
            public void ExportObjects()
            {
                try
                {
                    Directory.CreateDirectory("barodepth");
                    using var w = new StreamWriter("barodepth/objects.csv", append: false);
                    w.WriteLine("entityId;prefab;name");
                    foreach (var item in Item.ItemList)
                    {
                        if (item == null || item.Removed) continue;
                        string prefabStr = item.Prefab != null ? item.Prefab.Identifier.Value : "?";
                        w.WriteLine($"{item.ID};{prefabStr};{item.Name}");
                    }
                    Log("Export done.", Color.Lime);
                }
                catch (Exception ex) { Log("Export: " + ex.Message, Color.Red); }
            }
            public void Dispose() { }
        }

        private static class PixelTex
        {
            private static Texture2D tex;
            public static Texture2D Get
            {
                get
                {
                    if (tex != null) return tex;
                    tex = new Texture2D(GameMain.Instance.GraphicsDevice, 1, 1);
                    tex.SetData(new[] { Color.White });
                    return tex;
                }
            }
        }
    }
}
