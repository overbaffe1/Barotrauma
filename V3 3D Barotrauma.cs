// BaroDepth 0.3 — an experimental client-only 2D -> 3D renderer for Barotrauma.
// Requires the same ACsMod / LuaCs C# loader as the supplied FirstPersonMod.cs.
// F5: on/off. F6: Normal/Full/Xray. F7: reload BaroDepth.xml. F8: export IDs. Alt: labels.
// Native movement, reach and access checks. Added LMB interaction. Physics and joints are not modified.
// Do not load together with another first-person/camera mod.
// Original implementation; no game assets or game source are bundled.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System.IO;
using System.Text;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BaroDepth
{
    public sealed class FirstPerson3D : ACsMod
    {
        // Tweak these first, rather than adding another experimental branch.
        private const float MouseSensitivity = 0.0027f;
        private const float FieldOfViewDegrees = 78f; // vertical FOV
        private const float CorridorHalfDepth = 155f;
        private const float ViewDistance = 2000f;     // display units, NOT physics units
        private const string Prefix = "barodepth.v01.";

        private enum RenderMode { Boxes, Contours, Slices }
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly List<HookRegistration> hooks = new List<HookRegistration>();
        private Renderer renderer;
        private bool enabled;
        private bool stopped;
        private bool mouseCaptured;
        private bool discardNextMouseDelta;
        private bool warnedAboutCursor;
        private KeyboardState previousKeyboard;
        private Character owner;
        private readonly RenderMode mode = RenderMode.Contours;
        private Settings settings = new Settings();
        private ViewMode viewMode = ViewMode.Full;
        private bool showHints;
        private bool clickPending;
        private bool worldClickArmed;
        private bool interactionHookAvailable;
        private Point clickPosition;
        private ButtonState previousLeft;
        private readonly NativeInteraction interaction = new NativeInteraction();
        private float yaw;
        private float pitch;
        private Vector2 savedCursor;
        private Point savedMouse;
        private bool ready;
        private static bool debugDoors;
        private static double nextDoorLog;
        private static readonly FieldInfo doorSpriteField = typeof(Door).GetField(
            "doorSprite", BindingFlags.NonPublic | BindingFlags.Instance);
        private bool warmupQueued = true;

        private enum ViewMode { Normal, Full, Xray }
        private sealed class ObjectRule
        {
            public string Prefab, Submarine, Side;
            public int? EntityId, EditorId;
            public float? Offset, Depth, Scale;
            public int Order;
            public int Specificity => (Prefab == null ? 0 : 1) + (Submarine == null ? 0 : 2) + (EditorId.HasValue ? 8 : 0) + (EntityId.HasValue ? 16 : 0);
            public bool Matches(EntityKey key)
            {
                return (Prefab == null || string.Equals(Prefab, key.Prefab, StringComparison.OrdinalIgnoreCase)) &&
                    (Submarine == null || string.Equals(Submarine, key.Submarine, StringComparison.OrdinalIgnoreCase)) &&
                    (!EntityId.HasValue || EntityId == key.RuntimeId) && (!EditorId.HasValue || EditorId == key.EditorId);
            }
        }
        private sealed class Settings
        {
            public float DepthScale = 0.25f, RoomHalfDepth = 155f, LayerSpread = 16f, SurfaceInset = 28f;
            public int FullTextureSize = 128, DetailTextureSize = 320, CacheMiB = 192, JobsPerFrame = 4;
            public double CaptureBudgetMs = 4;
            public bool ShowIds = true;
            public bool WarmupAll = true;
            public bool SurfaceTexture = true;
            public float ContourRefreshScale = 1f;
            public ViewMode StartMode = ViewMode.Full;
            public readonly List<ObjectRule> Rules = new List<ObjectRule>();
            private readonly Dictionary<string, List<ObjectRule>> indexed = new Dictionary<string, List<ObjectRule>>(StringComparer.OrdinalIgnoreCase);
            private static string SavedKey(string submarine, int id) { return "s:" + submarine + "#" + id; }
            public void IndexRules()
            {
                indexed.Clear();
                for (int i = 0; i < Rules.Count; i++)
                {
                    ObjectRule rule = Rules[i]; rule.Order = i;
                    string key = rule.EntityId.HasValue ? "e:" + rule.EntityId.Value : rule.EditorId.HasValue
                        ? SavedKey(rule.Submarine, rule.EditorId.Value) : "p:" + rule.Prefab;
                    if (!indexed.TryGetValue(key, out List<ObjectRule> bucket)) { bucket = new List<ObjectRule>(); indexed[key] = bucket; }
                    bucket.Add(rule);
                }
            }
            public ObjectRule Resolve(EntityKey key)
            {
                var candidates = new List<ObjectRule>();
                void Collect(string index)
                { if (indexed.TryGetValue(index, out List<ObjectRule> bucket)) foreach (ObjectRule r in bucket) if (r.Matches(key)) candidates.Add(r); }
                Collect("p:" + key.Prefab); Collect("e:" + key.RuntimeId);
                if (key.EditorId.HasValue) Collect(SavedKey(key.Submarine, key.EditorId.Value));
                candidates.Sort((a, b) => { int p = a.Specificity.CompareTo(b.Specificity); return p != 0 ? p : a.Order.CompareTo(b.Order); });
                var result = new ObjectRule { Offset = 0f, Scale = 1f, Side = "both" };
                foreach (ObjectRule rule in candidates)
                {
                    if (rule.Offset.HasValue) result.Offset = rule.Offset;
                    if (rule.Depth.HasValue) result.Depth = rule.Depth;
                    if (rule.Scale.HasValue) result.Scale = rule.Scale;
                    if (rule.Side != null) result.Side = rule.Side;
                }
                return result;
            }
        }
        private static class SettingsStore
        {
            public static string ActivePath;
            public static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Daedalic Entertainment GmbH", "Barotrauma", "BaroDepth");
            private static string ChoosePath()
            {
                string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BaroDepth.xml");
                return File.Exists(portable) ? portable : Path.Combine(UserFolder, "BaroDepth.xml");
            }
            public static bool TryLoad(out Settings result, out string message)
            {
                result = null;
                try
                {
                    string path = ChoosePath();
                    if (!File.Exists(path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(DefaultXml);
                    }
                    XDocument doc;
                    var options = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2000000 };
                    using (XmlReader reader = XmlReader.Create(path, options)) doc = XDocument.Load(reader);
                    XElement root = doc.Root;
                    if (root == null || root.Name != "BaroDepth") throw new FormatException("Root must be <BaroDepth> without a namespace.");
                    Settings next = new Settings();
                    XElement global = root.Element("Global");
                    if (global != null)
                    {
                        CheckAttributes(global, "depthScale", "roomHalfDepth", "layerSpread", "surfaceInset", "defaultMode", "showIds",
                            "fullTextureSize", "detailTextureSize", "cacheMiB", "jobsPerFrame", "captureBudgetMs", "warmupAll", "surfaceTexture", "contourRefreshScale");
                        next.DepthScale = Number(global, "depthScale", next.DepthScale, 0f, 4f);
                        next.RoomHalfDepth = Number(global, "roomHalfDepth", next.RoomHalfDepth, 50f, 600f);
                        next.LayerSpread = Number(global, "layerSpread", next.LayerSpread, 0f, 200f);
                        next.SurfaceInset = Number(global, "surfaceInset", next.SurfaceInset, 1f, 200f);
                        next.FullTextureSize = Integer(global, "fullTextureSize", next.FullTextureSize, 32, 256);
                        next.DetailTextureSize = Integer(global, "detailTextureSize", next.DetailTextureSize, next.FullTextureSize, 512);
                        next.CacheMiB = Integer(global, "cacheMiB", next.CacheMiB, 32, 2048);
                        next.JobsPerFrame = Integer(global, "jobsPerFrame", next.JobsPerFrame, 2, 12);
                        next.CaptureBudgetMs = Number(global, "captureBudgetMs", (float)next.CaptureBudgetMs, 1f, 20f);
                        next.ShowIds = Boolean(global, "showIds", true);
                        next.WarmupAll = Boolean(global, "warmupAll", next.WarmupAll);
                        next.SurfaceTexture = Boolean(global, "surfaceTexture", next.SurfaceTexture);
                        next.ContourRefreshScale = Number(global, "contourRefreshScale", next.ContourRefreshScale, 0.1f, 10f);
                        string mode = (string)global.Attribute("defaultMode");
                        if (mode != null && (!Enum.TryParse(mode, true, out next.StartMode) || !Enum.IsDefined(typeof(ViewMode), next.StartMode)))
                            throw new FormatException("defaultMode: Normal, Full or Xray.");
                    }
                    XElement objects = root.Element("Objects");
                    if (objects != null)
                    foreach (XElement element in objects.Elements())
                    {
                        if (element.Name != "Object") throw new FormatException("Use <Object> inside <Objects>.");
                        CheckAttributes(element, "prefab", "submarine", "entityId", "editorId", "offset", "depth", "scale", "side", "enabled");
                        if (!Boolean(element, "enabled", true)) continue;
                        var rule = new ObjectRule
                        {
                            Prefab = Text(element, "prefab"), Submarine = Text(element, "submarine"),
                            EntityId = OptionalId(element, "entityId"), EditorId = OptionalId(element, "editorId"),
                            Offset = OptionalNumber(element, "offset", -1000f, 1000f), Depth = OptionalNumber(element, "depth", 0f, 1000f),
                            Scale = OptionalNumber(element, "scale", 0f, 10f), Side = Text(element, "side")
                        };
                        if (rule.Prefab == null && !rule.EntityId.HasValue && !rule.EditorId.HasValue)
                            throw new FormatException("Object needs prefab, entityId or editorId.");
                        if (rule.EditorId.HasValue && rule.Submarine == null)
                            throw new FormatException("editorId also needs submarine.");
                        if (rule.Side != null)
                        {
                            rule.Side = rule.Side.ToLowerInvariant();
                            if (rule.Side != "both" && rule.Side != "positive" && rule.Side != "negative" && rule.Side != "hide")
                                throw new FormatException("side: both, positive, negative or hide.");
                        }
                        next.Rules.Add(rule);
                        if (next.Rules.Count > 10000) throw new FormatException("Maximum 10000 object rules.");
                    }
                    int globals = 0, objectSections = 0;
                    foreach (XElement child in root.Elements())
                    {
                        if (child.Name == "Global") globals++;
                        else if (child.Name == "Objects") objectSections++;
                        else throw new FormatException("Unknown section: " + child.Name.LocalName);
                    }
                    if (globals > 1 || objectSections > 1) throw new FormatException("Use one Global and one Objects section.");
                    next.IndexRules(); ActivePath = path; result = next;
                    message = "Loaded " + next.Rules.Count + " rules: " + path; return true;
                }
                catch (Exception ex) { message = "BaroDepth.xml: " + ex.Message + " (previous settings retained)"; return false; }
            }
            private static bool Boolean(XElement element, string name, bool fallback)
            {
                string raw = (string)element.Attribute(name); if (raw == null) return fallback;
                if (!bool.TryParse(raw, out bool value)) throw new FormatException(name + ": true or false.");
                return value;
            }
            private static string Text(XElement element, string name)
            { string value = ((string)element.Attribute(name))?.Trim(); return string.IsNullOrEmpty(value) ? null : value; }
            private static void CheckAttributes(XElement element, params string[] allowed)
            {
                foreach (XAttribute attribute in element.Attributes())
                {
                    bool found = false;
                    foreach (string name in allowed) if (attribute.Name == name) { found = true; break; }
                    if (!found) throw new FormatException("Unknown setting: " + attribute.Name.LocalName);
                }
            }
            private static float Number(XElement element, string name, float fallback, float min, float max)
            {
                string raw = (string)element.Attribute(name); if (raw == null) return fallback;
                if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
                    float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
                    throw new FormatException(name + " must be " + min.ToString(CultureInfo.InvariantCulture) + ".." + max.ToString(CultureInfo.InvariantCulture) + " (decimal dot).");
                return value;
            }
            private static int Integer(XElement element, string name, int fallback, int min, int max)
            {
                float value = Number(element, name, fallback, min, max);
                if (value != (int)value) throw new FormatException(name + " must be an integer.");
                return (int)value;
            }
            private static int? OptionalId(XElement element, string name)
            { return element.Attribute(name) == null ? (int?)null : Integer(element, name, 0, 0, 65535); }
            private static float? OptionalNumber(XElement element, string name, float min, float max)
            { return element.Attribute(name) == null ? (float?)null : Number(element, name, 0f, min, max); }
            public const string DefaultXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<BaroDepth version=""1"">
  <!-- F7: перечитать этот файл. Десятичный разделитель — точка. -->
  <!-- depthScale 0.25 = объём предметов в 4 раза тоньше; 1 = исходная толщина. -->
  <!-- roomHalfDepth задаёт ПОЛОВИНУ ширины коридора. Соседние предметы её не меняют. -->
  <!-- offset > 0 приближает предмет к середине; offset < 0 двигает к стенке. -->
  <Global depthScale=""0.25"" roomHalfDepth=""155"" layerSpread=""16"" surfaceInset=""28""
          defaultMode=""Full"" showIds=""true""
          fullTextureSize=""128"" detailTextureSize=""320"" cacheMiB=""192""
          jobsPerFrame=""4"" captureBudgetMs=""4"" warmupAll=""true"" surfaceTexture=""true"" contourRefreshScale=""1"" />

  <Objects>
    <!-- ПРИМЕРЫ ниже закомментированы и пока ничего не меняют. -->
    <!-- prefab — строковый identifier типа предмета из редактора, а не его название. -->
    <!-- entityId — числовой ID текущего экземпляра в игре. Может меняться между раундами. -->
    <!-- editorId — ID из файла лодки; обязательно укажи submarine. Смотри выгрузку F8. -->
    <!-- offset: единицы смещения, НЕ умножается на depthScale. -->
    <!-- depth: полная толщина ДО глобального масштаба; scale: дополнительный множитель. -->
    <!-- Итоговая толщина = depth * depthScale * scale. Без depth берётся толщина по размеру. -->
    <!-- Оба значения ограничены внутренней шириной комнаты, которую предмет не расширяет. -->
    <!-- side: both / positive / negative / hide (две стороны Z, одна или скрыть). -->

    <!-- Все распределительные коробки: ближе на 8 единиц, итоговая толщина 16*0.25=4. -->
    <!-- <Object prefab=""junctionbox"" offset=""8"" depth=""16"" /> -->

    <!-- Только один предмет текущего раунда (замени 1234 настоящим entityId из F8): -->
    <!-- <Object entityId=""1234"" offset=""18"" scale=""0.5"" /> -->

    <!-- Предмет из сохранённой лодки (замени имя лодки и editorId): -->
    <!-- <Object submarine=""MySub"" editorId=""123"" offset=""-6"" depth=""32"" /> -->

    <!-- Только один визуальный дубль указанного предмета: -->
    <!-- <Object entityId=""1234"" side=""positive"" /> -->
  </Objects>
</BaroDepth>
";
        }

        private sealed class EntityKey
        {
            public int RuntimeId;
            public int? EditorId;
            public string Prefab, Submarine;
        }

        private sealed class IdentityMap
        {
            private sealed class SavedEntry { public int Id; public string Prefab; }
            private readonly Dictionary<Submarine, Dictionary<int, SavedEntry>> saved = new Dictionary<Submarine, Dictionary<int, SavedEntry>>();
            private bool warned;
            public void Clear() { saved.Clear(); warned = false; }
            public EntityKey Get(MapEntity entity)
            {
                string prefab = entity.Prefab?.Identifier.ToString() ?? "";
                var key = new EntityKey { RuntimeId = entity.ID, Prefab = prefab,
                    Submarine = entity.Submarine?.Info?.Name ?? "" };
                Submarine sub = entity.Submarine;
                if (sub == null) return key;
                if (!saved.TryGetValue(sub, out Dictionary<int, SavedEntry> map))
                { map = ReadMap(sub); saved[sub] = map; }
                if (map.TryGetValue(entity.ID, out SavedEntry entry) &&
                    (entry.Prefab == null || string.Equals(entry.Prefab, prefab, StringComparison.OrdinalIgnoreCase))) key.EditorId = entry.Id;
                return key;
            }
            private Dictionary<int, SavedEntry> ReadMap(Submarine sub)
            {
                var result = new Dictionary<int, SavedEntry>();
                try
                {
                    XElement root = sub.Info?.SubmarineElement;
                    if (root == null) return result;
                    // Reuse the game's saved-ID remapper. ID - IdOffset is NOT generally
                    // the editor ID: non-contiguous saved IDs can be compacted on loading.
                    MethodInfo links = typeof(MapEntity).GetMethod("ParseLinks", BindingFlags.NonPublic | BindingFlags.Instance);
                    Type remapType = links?.GetParameters().Length == 2 ? links.GetParameters()[1].ParameterType : null;
                    ConstructorInfo ctor = remapType?.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { typeof(XElement), typeof(int) }, null);
                    MethodInfo offset = null;
                    if (remapType != null)
                        foreach (MethodInfo method in remapType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            var parameters = method.GetParameters();
                            if (method.Name == "GetOffsetId" && parameters.Length == 1 &&
                                (parameters[0].ParameterType == typeof(int) || parameters[0].ParameterType == typeof(ushort)))
                            { offset = method; break; }
                        }
                    if (ctor == null || offset == null) throw new NotSupportedException("Saved-ID mapping unavailable; use prefab/entityId from F8.");
                    object remap = ctor.Invoke(new object[] { root, (int)sub.IdOffset });
                    Type inputType = offset.GetParameters()[0].ParameterType;
                    foreach (XElement element in root.Elements())
                    {
                        string raw = (string)element.Attribute("ID") ?? (string)element.Attribute("id");
                        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sourceId) || sourceId < 0 || sourceId > 65535) continue;
                        int runtimeId = Convert.ToInt32(offset.Invoke(remap, new[] { Convert.ChangeType(sourceId, inputType, CultureInfo.InvariantCulture) }), CultureInfo.InvariantCulture);
                        if (runtimeId <= 0 || runtimeId > 65535) continue;
                        result[runtimeId] = new SavedEntry { Id = sourceId, Prefab = (string)element.Attribute("identifier") };
                    }
                }
                catch (Exception ex)
                {
                    if (!warned) { warned = true; Log("Editor IDs: " + ex.GetBaseException().Message, Color.Orange); }
                }
                return result;
            }
        }

        private void ReloadSettings(bool initial = false)
        {
            if (SettingsStore.TryLoad(out Settings loaded, out string message))
            {
                settings = loaded; viewMode = loaded.StartMode;
                renderer?.ApplySettings(loaded);
                Log(message, Color.Cyan);
                renderer?.Notify("Settings reloaded | depth x" + loaded.DepthScale.ToString("0.###", CultureInfo.InvariantCulture), Color.Cyan);
            }
            else
            {
                Log(message, Color.Orange);
                renderer?.Notify("XML error — settings unchanged (F3)", Color.Orange);
            }
        }

        private sealed class HookRegistration
        {
            public string Id;
            public MethodInfo Method;
        }

        public FirstPerson3D()
        {
            ReloadSettings(true);
            previousKeyboard = Keyboard.GetState();
            try
            {
                // The supplied fork draws HUD after DrawMap. We replace only the completed
                // world image, never Camera.Transform, light-manager flags or physics state.
                MethodInfo drawMap = FindMethod(typeof(GameScreen), "DrawMap", 3);
                MethodInfo draw = FindMethod(typeof(GameScreen), "Draw", 3);
                if (drawMap == null || draw == null)
                throw new MissingMethodException("GameScreen.DrawMap/Draw: this game build has a different API.");

                AddAfter("map", drawMap, (instance, arguments) =>
                    {
                        if (stopped || !enabled) return null;
                        if (!HasPlayableContext()) { Disable(false); return null; }
                        try
                        {
                            if (renderer == null) renderer = new Renderer(settings, ViewDistance);
                            if (warmupQueued) { renderer.WarmupPending = true; warmupQueued = false; }
                            renderer.Render(owner, mode, viewMode, yaw, pitch, FieldOfViewDegrees, clock.Elapsed.TotalSeconds);
                        }
                        catch (Exception ex) { Fail("render", ex); }
                        return null;
                    });
                AddAfter("hud", draw, (instance, arguments) =>
                    {
                        if (stopped || !enabled || renderer == null || !HasPlayableContext()) return null;
                        try { renderer.DrawOverlay(mode, mouseCaptured, owner.InWater, showHints); }
                        catch (Exception ex) { Fail("overlay", ex); }
                        return null;
                    });

                // Run native interaction after the game samples this character's keys.
                // Signatures are checked; range/access/required-item checks stay native.
                MethodInfo cursorUpdate = FindMethod(typeof(Character), "UpdateLocalCursor", 1);
                if (cursorUpdate != null)
                {
                    AddAfter("cursor", cursorUpdate, (instance, arguments) =>
                        {
                            if (stopped || !enabled || !ReferenceEquals(instance, owner)) return null;
                            try { UpdateAim(); }
                            catch (Exception ex) { Fail("aim", ex); }
                            return null;
                        });
                }
                else
                {
                    warnedAboutCursor = true;
                    Log("UpdateLocalCursor hook not found: aiming will run from the update hook.", Color.Orange);
                }
                MethodInfo localControl = FindMethod(typeof(Character), "ControlLocalPlayer", 3);
                if (localControl != null)
                {
                    AddAfter("interaction", localControl, (instance, arguments) =>
                        {
                            if (stopped || !enabled || !ReferenceEquals(instance, owner)) return null;
                            try { CommitInteraction(); }
                            catch (Exception ex) { Fail("interaction", ex); }
                            return null;
                        });
                    interactionHookAvailable = true;
                }
                else Log("Post-control interaction hook unavailable: use the native interaction key.", Color.Orange);
                GameMain.LuaCs.Hook.Add("think", Prefix + "update", (object[] arguments) =>
                    {
                        if (stopped) return null;
                        try { Update(); }
                        catch (Exception ex) { Fail("update", ex); }
                        return null;
                    });
                ready = true;
                Log("Ready 0.3. F5: 3D | F6: Normal/Full/Xray | F7: reload XML | F8: export IDs | Left Alt: labels/pointer | LMB: interact. Native movement.", Color.Cyan);
            }
            catch (Exception ex)
            {
                stopped = true;
                RemoveHooks();
                Log("Not installed: " + ex, Color.OrangeRed);
            }
        }

        private static MethodInfo FindMethod(Type type, string name, int parameterCount)
        {
            MethodInfo found = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != name || method.GetParameters().Length != parameterCount) continue;
                if (found != null) return null; // ambiguous API: fail explicitly, do not guess
                found = method;
            }
            return found;
        }

        private void AddAfter(string suffix, MethodInfo method, Func<object, Dictionary<string, object>, object> callback)
        {
            string id = Prefix + suffix;
            GameMain.LuaCs.Hook.HookMethod(id, method,
                (object instance, Dictionary<string, object> arguments) => callback(instance, arguments),
                LuaCsHook.HookMethodType.After);
            hooks.Add(new HookRegistration { Id = id, Method = method });
        }

        private bool HasPlayableContext()
        {
            return owner != null && !owner.Removed && !owner.IsDead &&
            ReferenceEquals(Character.Controlled, owner) && Screen.Selected is GameScreen;
        }

        private static bool UiOwnsInput()
        {
            return GUI.InputBlockingMenuOpen || GUI.PauseMenuOpen ||
            GUI.KeyboardDispatcher?.Subscriber != null || GameMain.Instance.Paused;
        }

        private static bool PointerOnUi()
        {
            return GUI.MouseOn != null || CharacterInventory.IsMouseOnInventory;
        }

        private bool HasItemGui()
        {
            Item selected = owner?.SelectedItem;
            if (selected == null || selected.Removed) return false;
            foreach (var component in selected.Components)
            if (component?.GuiFrame != null && component.ShouldDrawHUD(owner)) return true;
            return false;
        }

        private void Update()
        {
            KeyboardState keyboard = Keyboard.GetState();
            MouseState mouse = Mouse.GetState();
            bool toggle = keyboard.IsKeyDown(Keys.F5) && previousKeyboard.IsKeyUp(Keys.F5);
            bool toggleXray = keyboard.IsKeyDown(Keys.F6) && previousKeyboard.IsKeyUp(Keys.F6);
            bool reload = keyboard.IsKeyDown(Keys.F7) && previousKeyboard.IsKeyUp(Keys.F7);
            bool export = keyboard.IsKeyDown(Keys.F8) && previousKeyboard.IsKeyUp(Keys.F8);
            bool pressed = mouse.LeftButton == ButtonState.Pressed && previousLeft == ButtonState.Released;
            bool released = mouse.LeftButton == ButtonState.Released && previousLeft == ButtonState.Pressed;
            previousKeyboard = keyboard; previousLeft = mouse.LeftButton;
            if (enabled && !HasPlayableContext()) Disable(false);
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
            if (reload && !UiOwnsInput()) { ReloadSettings(); warmupQueued = true; }
            if (keyboard.IsKeyDown(Keys.F2) && previousKeyboard.IsKeyUp(Keys.F2))
            {
                debugDoors = !debugDoors;
                nextDoorLog = 0; // print immediately on toggle
                Log("door debug: " + (debugDoors ? "ON" : "OFF"), Color.Cyan);
            }
            if (export && !UiOwnsInput() && renderer != null) renderer.ExportObjects();
            bool ui = UiOwnsInput() || HasItemGui();
            if (toggleXray && !UiOwnsInput())
            {
                viewMode = (ViewMode)(((int)viewMode + 1) % 3); renderer?.InvalidateView();
                warmupQueued = true;
                Log("CONTOURS / " + viewMode.ToString().ToUpperInvariant(), Color.Cyan);
            }
            showHints = keyboard.IsKeyDown(Keys.LeftAlt) && !ui;
            bool capture = !ui && !showHints;
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
            Point pointer = capture ? new Point(cx, cy) : new Point(mouse.X, mouse.Y);
            bool worldInput = !ui && !PointerOnUi() &&
            (showHints || !PlayerInput.KeyDown(InputType.Aim));
            if (!worldInput) { worldClickArmed = false; clickPending = false; }
            if (pressed && worldInput && interactionHookAvailable && renderer != null)
            {
                Item target; Vector2 physical;
                worldClickArmed = renderer.TryPick(pointer, yaw, pitch, showHints, out target, out physical);
            }
            if (released && worldClickArmed && worldInput)
            {
                // Release edge is committed AFTER ControlLocalPlayer has processed
                // HUD.CloseHUD. Opening on press let the release immediately close it.
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

        private void Enable()
        {
            Character character = Character.Controlled;
            if (!(Screen.Selected is GameScreen) || character == null || character.IsDead || character.Removed)
            {
                Log("F5: control a living character in a running round first.", Color.Orange);
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
            warmupQueued = true;   // F5: full capture immediately, not a slow warm-up
            discardNextMouseDelta = true;
            Log("ON / " + ModeName(mode) + (warnedAboutCursor ? " / aim hook unavailable" : ""), Color.LimeGreen);
        }

        private void Disable(bool announce)
        {
            if (!enabled) return;
            enabled = false; // callbacks become inert BEFORE any restoration/disposal
            clickPending = false; showHints = false; worldClickArmed = false;
            mouseCaptured = false;
            discardNextMouseDelta = true;
            if (owner != null && !owner.Removed && ReferenceEquals(owner, Character.Controlled))
            owner.CursorPosition = savedCursor;
            owner = null;
            if (GameMain.WindowActive) Mouse.SetPosition(savedMouse.X, savedMouse.Y);
            renderer?.ClearScene();
            if (announce) Log("OFF / original 2D view. Camera, lighting and physics were not replaced.", Color.Orange);
        }

        private void UpdateAim()
        {
            if (!HasPlayableContext() || UiOwnsInput() || HasItemGui() || !GameMain.WindowActive)
            { clickPending = false; worldClickArmed = false; return; }
            if (PointerOnUi() && !mouseCaptured)
            { clickPending = false; worldClickArmed = false; renderer?.SetHover(null); return; }
            Point pointer = mouseCaptured
            ? new Point(GameMain.GraphicsWidth / 2, GameMain.GraphicsHeight / 2)
            : new Point(Mouse.GetState().X, Mouse.GetState().Y);
            Item target = null; Vector2 targetWorld = Vector2.Zero;
            bool picked = renderer != null && renderer.TryPick(pointer, yaw, pitch, false, out target, out targetWorld);
            renderer?.SetHover(picked ? target : null);
            if (picked && mouseCaptured)
            owner.CursorPosition = owner.Position + targetWorld - owner.WorldPosition;
            else if (mouseCaptured)
            {
                Vector3 forward = Forward(yaw, pitch);
                Vector2 direction = new Vector2(forward.X, forward.Y);
                if (direction.LengthSquared() >= 0.015f)
                {
                    direction.Normalize();
                    targetWorld = PhysicalHead(owner) + direction * 350f;
                    owner.CursorPosition = owner.Position + targetWorld - owner.WorldPosition;
                }
            }
            // Prevent the same mouse gesture being handled by native 2D picking first.
            // This runs before DoInteractionUpdate, for this controlled character only.
            if (worldClickArmed || clickPending)
            { ClearClickKey("Shoot"); ClearClickKey("Use"); ClearClickKey("Select"); }
        }

        private void CommitInteraction()
        {
            if (!clickPending) return;
            clickPending = false;
            if (!HasPlayableContext() || renderer == null || !GameMain.WindowActive ||
                UiOwnsInput() || PointerOnUi() || HasItemGui()) return;
            if (!renderer.TryPick(clickPosition, yaw, pitch, showHints, out Item target, out Vector2 targetWorld)) return;
            owner.CursorPosition = owner.Position + targetWorld - owner.WorldPosition;
            if (!owner.CanInteractWith(target))
            {
                renderer.Notify("Too far / blocked / interaction unavailable", Color.Orange);
                return;
            }
            // Keep native focus and the one-frame Select input coherent for networking.
            // Do NOT clear Select after the call: that discarded the server-side action.
            owner.FocusedItem = target;
            owner.EmulateInput(InputType.Select);
            string error;
            if (interaction.TrySelect(target, owner, out error))
            {
                renderer.Notify("Interact: " + target.Name, Color.LimeGreen);
                if (HasItemGui())
                { mouseCaptured = false; discardNextMouseDelta = true; showHints = false; }
            }
            else renderer.Notify(error, Color.Orange);
        }

        private void ClearClickKey(string name)
        {
            if (!ReferenceEquals(Character.Controlled, owner) || owner?.Keys == null) return;
            if (!Enum.TryParse(name, out InputType type)) return;
            int index = (int)type;
            if (index < 0 || index >= owner.Keys.Length || owner.Keys[index] == null) return;
            owner.Keys[index].Held = false;
            owner.Keys[index].Hit = false;
        }

        private sealed class NativeInteraction
        {
            private readonly MethodInfo method;
            private readonly ParameterInfo[] parameters;
            public NativeInteraction()
            {
                foreach (MethodInfo candidate in typeof(Item).GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (candidate.Name != "TryInteract" || candidate.ReturnType != typeof(bool)) continue;
                    ParameterInfo[] p = candidate.GetParameters();
                    if (p.Length == 0 || p[0].ParameterType != typeof(Character)) continue;
                    bool valid = true, select = false;
                    for (int i = 1; i < p.Length; i++)
                    {
                        if (p[i].ParameterType != typeof(bool)) { valid = false; break; }
                        string name = p[i].Name;
                        if (name == "forceSelectKey") select = true;
                        else if (name != "ignoreRequiredItems" && name != "forceUseKey")
                        valid = false;
                    }
                    if (valid && select) { method = candidate; parameters = p; break; }
                }
            }
            public bool TrySelect(Item item, Character character, out string error)
            {
                error = "Native interaction is unavailable for this object";
                if (method == null)
                { error = "TryInteract API not found; use the game's normal interaction key"; return false; }
                object[] args = new object[parameters.Length]; args[0] = character;
                for (int i = 1; i < args.Length; i++)
                {
                    string name = parameters[i].Name;
                    if (name == "forceSelectKey") args[i] = true;
                    else if (name == "ignoreRequiredItems" || name == "forceUseKey") args[i] = false;
                    else args[i] = parameters[i].DefaultValue;
                }
                try { return (bool)method.Invoke(item, args); }
                catch (Exception ex)
                {
                    Exception actual = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    error = "Interaction error; details in F3";
                    Log("TryInteract: " + actual, Color.OrangeRed);
                    return false;
                }
            }
        }

        private void Fail(string stage, Exception ex)
        {
            try { Disable(false); }
            catch (Exception cleanup) { Log("Cleanup: " + cleanup, Color.OrangeRed); }
            try { renderer?.Dispose(); }
            catch (Exception cleanup) { Log("Resource cleanup: " + cleanup.Message, Color.OrangeRed); }
            renderer = null; // a broken SpriteBatch must not survive the next F5 activation
            Log("Disabled after " + stage + " error. Copy this from F3:\n" + ex, Color.OrangeRed);
        }

        public override void Stop()
        {
            if (stopped) return;
            stopped = true;
            try { Disable(false); }
            finally
            {
                try { RemoveHooks(); }
                finally { renderer?.Dispose(); renderer = null; }
            }
        }

        private void RemoveHooks()
        {
            try { GameMain.LuaCs.Hook.Remove("think", Prefix + "update"); }
            catch (Exception ex) { Log("Update hook cleanup: " + ex.Message, Color.Orange); }
            // Hook-removal overloads differ between LuaCs releases. Match a supported
            // signature, rather than referencing a method that may not compile there.
            object hookManager = GameMain.LuaCs.Hook;
            foreach (HookRegistration registration in hooks)
            {
                bool removed = false;
                foreach (MethodInfo method in hookManager.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (method.Name != "UnhookMethod" && method.Name != "RemovePatch") continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    object[] args = new object[parameters.Length];
                    bool valid = parameters.Length >= 1;
                    bool hasId = false;
                    for (int i = 0; i < parameters.Length && valid; i++)
                    {
                        Type type = parameters[i].ParameterType;
                        if (type == typeof(string) && !hasId) { args[i] = registration.Id; hasId = true; }
                        else if (typeof(MethodBase).IsAssignableFrom(type)) args[i] = registration.Method;
                        else if (type == typeof(LuaCsHook.HookMethodType)) args[i] = LuaCsHook.HookMethodType.After;
                        else if (parameters[i].HasDefaultValue) args[i] = parameters[i].DefaultValue;
                        else valid = false;
                    }
                    if (!valid || !hasId) continue;
                    try { method.Invoke(hookManager, args); removed = true; break; }
                    catch (Exception ex) { Log("Hook cleanup " + registration.Id + ": " + ex.Message, Color.Orange); }
                }
                if (!removed)
                Log("This LuaCs build exposes no matching unhook for " + registration.Id +
                    ". Callback is disabled; restart the game before reloading this mod.", Color.Orange);
            }
            hooks.Clear();
        }

        private static string ModeName(RenderMode value)
        {
            return "CONTOURS";
        }

        private static Vector3 Forward(float y, float p)
        {
            float cp = (float)Math.Cos(p);
            return new Vector3((float)Math.Cos(y) * cp, (float)Math.Sin(p), (float)Math.Sin(y) * cp);
        }

        private static Vector2 PhysicalHead(Character character)
        {
            if (character.AnimController?.Limbs != null)
            foreach (Limb limb in character.AnimController.Limbs)
            if (limb != null && limb.type == LimbType.Head && limb.body != null) return limb.WorldPosition;
            return character.WorldPosition;
        }

        private static Vector2 DrawHead(Character character)
        {
            if (character.AnimController?.Limbs != null)
            foreach (Limb limb in character.AnimController.Limbs)
            if (limb != null && limb.type == LimbType.Head && limb.body != null) return limb.body.DrawPosition;
            return character.WorldPosition + SubDrawCorrection(character.Submarine);
        }

        private static Vector2 SubDrawCorrection(Submarine sub)
        {
            return sub == null ? Vector2.Zero : sub.DrawPosition - sub.Position;
        }

        private static void Log(string message, Color color)
        {
            DebugConsole.NewMessage("[BaroDepth] " + message, color);
        }

        private struct Bounds2
        {
            public float Left, Bottom, Right, Top;
            public float Width => Right - Left;
            public float Height => Top - Bottom;
            public Vector2 Center => new Vector2((Left + Right) * 0.5f, (Bottom + Top) * 0.5f);
            public bool Valid => Width > 0.1f && Height > 0.1f &&
            !float.IsNaN(Width) && !float.IsInfinity(Width) &&
            !float.IsNaN(Height) && !float.IsInfinity(Height);
            public Bounds2(float left, float bottom, float right, float top)
            { Left = left; Bottom = bottom; Right = right; Top = top; }
            public Bounds2 Expanded(float amount)
            { return new Bounds2(Left - amount, Bottom - amount, Right + amount, Top + amount); }
            public float DistanceSquared(Vector2 point)
            {
                float dx = Math.Max(Left - point.X, Math.Max(0f, point.X - Right));
                float dy = Math.Max(Bottom - point.Y, Math.Max(0f, point.Y - Top));
                return dx * dx + dy * dy;
            }
        }

        private sealed class Stamp : IDisposable
        {
            public RenderTarget2D Texture;
            public Bounds2 Bounds;
            public Bounds2 ImageBounds;
            public Mesh Sides;
            public bool[] Mask;
            public int MaskWidth, MaskHeight, MaskStep, PixelWidth, PixelHeight;
            public Vector2 AnchorUV = new Vector2(0.5f, 0.5f);
            public double CapturedDoorState = -1;
            public bool HasImage;
            public bool DiagLogged;
            public bool Failed;
            public bool OutlineAttempted;
            public int Quality;
            public double RetryAfter;
            public int ShapeRevision;
            public double LastCapture = -1000;
            public double LastSeen;
            public double LastDrawn = -1000;
            public long Bytes => (Texture == null ? 0L : (long)Texture.Width * Texture.Height * 4L) + (Sides?.Bytes ?? 0L) + (Mask == null ? 0L : Mask.Length);
            public void Dispose()
            {
                Sides?.Dispose(); Sides = null;
                Texture?.Dispose(); Texture = null;
                HasImage = false; Mask = null;
            }
        }

        private sealed class Mesh : IDisposable
        {
            public VertexBuffer Vertices;
            public IndexBuffer Indices;
            public int VertexCount, TriangleCount;
            public long Bytes => (long)VertexCount * 24 + (long)TriangleCount * 6;
            public static Mesh Create(GraphicsDevice gd, List<VertexPositionColorTexture> vertices, List<short> indices)
            {
                if (vertices.Count == 0 || indices.Count == 0) return null;
                Mesh mesh = new Mesh { VertexCount = vertices.Count, TriangleCount = indices.Count / 3 };
                try
                {
                    mesh.Vertices = new VertexBuffer(gd, VertexPositionColorTexture.VertexDeclaration, vertices.Count, BufferUsage.WriteOnly);
                    mesh.Vertices.SetData(vertices.ToArray());
                    mesh.Indices = new IndexBuffer(gd, IndexElementSize.SixteenBits, indices.Count, BufferUsage.WriteOnly);
                    mesh.Indices.SetData(indices.ToArray());
                    return mesh;
                }
                catch { mesh.Dispose(); throw; }
            }
            public void Dispose() { Vertices?.Dispose(); Indices?.Dispose(); Vertices = null; Indices = null; }
        }

        // Restoring targets is not enough: a render-target switch also changes the viewport.
        private sealed class GraphicsScope : IDisposable
        {
            private readonly GraphicsDevice gd;
            private readonly RenderTargetBinding[] targets;
            private readonly Viewport viewport;
            private readonly Rectangle scissor;
            private readonly BlendState blend;
            private readonly DepthStencilState depth;
            private readonly RasterizerState rasterizer;
            private readonly SamplerState sampler;
            private readonly Texture texture;
            private readonly VertexBufferBinding[] vertices;
            private readonly IndexBuffer indices;
            private static readonly MethodInfo vertexBufferReader = typeof(GraphicsDevice).GetMethod(
                "GetVertexBuffers", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            private static bool bufferReadWarning;
            private static VertexBufferBinding[] ReadVertexBindings(GraphicsDevice device)
            {
                if (vertexBufferReader == null || vertexBufferReader.ReturnType != typeof(VertexBufferBinding[])) return null;
                try { return (VertexBufferBinding[])vertexBufferReader.Invoke(device, null); }
                catch (Exception ex) { if (!bufferReadWarning) { bufferReadWarning = true; Log("Optional buffer snapshot: " + ex.Message, Color.Orange); } return null; }
            }
            public GraphicsScope(GraphicsDevice device)
            {
                gd = device; targets = gd.GetRenderTargets(); viewport = gd.Viewport;
                scissor = gd.ScissorRectangle; blend = gd.BlendState; depth = gd.DepthStencilState;
                rasterizer = gd.RasterizerState; sampler = gd.SamplerStates[0]; texture = gd.Textures[0];
                vertices = ReadVertexBindings(gd); indices = gd.Indices;
            }
            public void RestoreTarget()
            {
                RenderTargetBinding[] current = gd.GetRenderTargets();
                bool same = current.Length == targets.Length;
                for (int i = 0; same && i < targets.Length; i++) same = current[i].Equals(targets[i]);
                if (!same) gd.SetRenderTargets(targets);
                gd.Viewport = viewport;
            }
            public void Dispose()
            {
                RestoreTarget();
                gd.ScissorRectangle = scissor;
                gd.BlendState = blend; gd.DepthStencilState = depth; gd.RasterizerState = rasterizer;
                gd.SamplerStates[0] = sampler; gd.Textures[0] = texture;
                if (vertices != null) gd.SetVertexBuffers(vertices);
                else gd.SetVertexBuffer(null);
                gd.Indices = indices;
            }
        }

        private sealed class Renderer : IDisposable
        {
            private const int MaxStaticEntries = 32768;
            private long MaxCacheBytes => (long)Math.Max(settings.CacheMiB, settings.WarmupAll ? 512 : settings.CacheMiB) * 1024L * 1024L;
            private const int MaxCharacters = 8;
            private const int MaxItems = 128; // detailed captures, NOT visibility cutoff
            private const int MaxStructures = 176; // detailed captures, NOT visibility cutoff
            private const int CaptureMaxSide = 320;
            private const int OutlineGridMaxSide = 80;
            private const byte AlphaCutoff = 128;
            private const int MaxSideQuads = 6000;

            private float halfDepth;
            private Settings settings;
            private readonly float viewDistance;
            private readonly GraphicsDevice gd;
            private SpriteBatch captureBatch;
            private readonly SpriteBatch screenBatch;
            private readonly BasicEffect solidEffect;
            private readonly AlphaTestEffect cutoutEffect;
            private readonly BlendState captureBlend;
            private readonly Camera captureCamera;
            // One synchronous capture pass over every loaded object on enable,
            // view switch or XML reload, so the whole sub is volumetric at once.
            public bool WarmupPending = true;
            private const int WarmupTextureSize = 0; // 0 = use settings.FullTextureSize
            // Labels ray-test every object; rebuilding them each frame while ALT
            // is held is the main source of the ALT stutter. Throttled instead.
            private double nextHintBuild;
            private readonly Dictionary<MapEntity, Stamp> staticStamps = new Dictionary<MapEntity, Stamp>();
            private readonly Dictionary<Character, Stamp> characterStamps = new Dictionary<Character, Stamp>();
            private readonly List<Structure> structures = new List<Structure>();
            private readonly List<Item> items = new List<Item>();
            private readonly List<Character> characters = new List<Character>();
            private readonly List<Hull> hulls = new List<Hull>();
            private readonly List<MapEntity> removals = new List<MapEntity>();
            private readonly List<Character> characterRemovals = new List<Character>();
            private readonly HashSet<string> warnings = new HashSet<string>();
            private readonly VertexPositionColorTexture[] quad = new VertexPositionColorTexture[4];
            private readonly short[] quadIndices = { 0, 1, 2, 2, 1, 3 };
            private Texture2D white;
            private Texture2D metal;
            private RenderTarget2D scene;
            private Vector2 eye;
            private Matrix view, projection;
            private double now, nextRefresh;
            private int mapCount = -1;
            private Submarine previousSub;
            private int frameDrawCalls, cachePending;
            private double lastFrameMs;
            private readonly Stopwatch frameTimer = new Stopwatch();
            private long cacheBytes;
            private bool disposed;
            private bool xrayView, fullView;
            private ViewMode sceneMode = (ViewMode)(-1);
            private float range, farPlane, expandedFarPlane;
            private Character currentPlayer;
            private float frameFov;
            private Item hoverItem;
            private string notice;
            private Color noticeColor;
            private double noticeUntil;
            private readonly Dictionary<MapEntity, LayerPlacement> layers = new Dictionary<MapEntity, LayerPlacement>();
            private readonly Dictionary<Structure, float> shellDepths = new Dictionary<Structure, float>();
            private readonly List<BackdropTile> backdrop = new List<BackdropTile>();
            private readonly Dictionary<Submarine, float> backWallDepths = new Dictionary<Submarine, float>();
            private readonly List<Hint> hintBoxes = new List<Hint>();
            private readonly List<Hint> hintCandidates = new List<Hint>();
            private readonly List<VertexPositionColor> xrayLines = new List<VertexPositionColor>();
            private VertexPositionColor[] lineBuffer = new VertexPositionColor[1024];
            private readonly Vector3[] wireCorners = new Vector3[8];
            private static readonly int[] wireEdges = { 0,1, 1,2, 2,3, 3,0, 4,5, 5,6, 6,7, 7,4, 0,4, 1,5, 2,6, 3,7 };
            private readonly LayerPlacement fallbackLayer = new LayerPlacement { Z = 130f, HalfDepth = 2.5f, Banks = 3 };
            private readonly RasterizerState capRasterizer = new RasterizerState
            { CullMode = CullMode.None, DepthBias = -0.00001f, SlopeScaleDepthBias = -0.25f };
            private readonly RasterizerState sideRasterizer = new RasterizerState
            { CullMode = CullMode.None, DepthBias = 0.00001f, SlopeScaleDepthBias = 0.25f };
            private static readonly MethodInfo depthReader = typeof(MapEntity).GetMethod(
                "GetDrawDepth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);

            private sealed class LayerPlacement
            {
                public MapEntity Entity;
                public Bounds2 Bounds;
                public float Native, Z, HalfDepth;
                public int Banks = 3;
                public EntityKey Key;
                public ObjectRule Rule;
                public bool RoomShell, Clamped;
                public float BaseDepth;
            }
            private sealed class BackdropTile
            {
                public Submarine Sub;
                public Bounds2 Local;
            }
            private sealed class Hint
            {
                public Item Item;
                public Vector2 Anchor;
                public Rectangle Box;
                public string Text;
                public float Score;
                public int Bank;
            }

            public Renderer(Settings configuration, float distance)
            {
                surfaceTexture = configuration.SurfaceTexture;
                contourRefreshScale = configuration.ContourRefreshScale;
                settings = configuration; halfDepth = settings.RoomHalfDepth; viewDistance = distance;
                gd = GameMain.Instance.GraphicsDevice;
                try
                {
                    captureBatch = new SpriteBatch(gd);
                    screenBatch = new SpriteBatch(gd);
                    solidEffect = new BasicEffect(gd) { TextureEnabled = true, VertexColorEnabled = true, LightingEnabled = false };
                    cutoutEffect = new AlphaTestEffect(gd)
                    {
                        VertexColorEnabled = true, AlphaFunction = CompareFunction.GreaterEqual,
                        ReferenceAlpha = AlphaCutoff
                    };
                    // Native sprites supply straight-alpha colors. Capture into premultiplied RGBA
                    // with correct alpha accumulation; do NOT square source alpha.
                    captureBlend = new BlendState
                    {
                        ColorSourceBlend = Blend.SourceAlpha,
                        ColorDestinationBlend = Blend.InverseSourceAlpha,
                        AlphaSourceBlend = Blend.One,
                        AlphaDestinationBlend = Blend.InverseSourceAlpha
                    };
                    captureCamera = new Camera { AutoUpdateToScreenResolution = false };
                    white = new Texture2D(gd, 1, 1); white.SetData(new[] { Color.White });
                    metal = CreateMetalTexture();
                }
                catch { Dispose(); throw; }
            }

            private Texture2D CreateMetalTexture()
            {
                const int size = 64;
                Color[] pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int grain = ((x * 17 + y * 31) % 7) - 3;
                    int c = 94 + grain;
                    if (x < 2 || y < 2) c = 49;
                    else if (x == 2 || y == 2) c = 119;
                    if ((x == 6 || x == 57) && (y == 6 || y == 57)) c = 160;
                    pixels[y * size + x] = new Color(c - 8, c, c + 8, 255);
                }
                Texture2D texture = new Texture2D(gd, size, size);
                texture.SetData(pixels);
                return texture;
            }

            public void Render(Character player, RenderMode mode, ViewMode sceneView, float yaw, float pitch, float fov, double time)
            {
                if (disposed) throw new ObjectDisposedException(nameof(Renderer));
                frameTimer.Restart(); now = time; frameDrawCalls = 0; cachePending = 0;
                currentPlayer = player; frameFov = fov;
                if (sceneMode != sceneView)
                { sceneMode = sceneView; xrayView = sceneView == ViewMode.Xray; fullView = sceneView != ViewMode.Normal; nextRefresh = 0; }
                range = fullView ? 24000f : viewDistance * 1.75f;
                farPlane = fullView ? Math.Max(range + 1000f, expandedFarPlane) : range + 1000f;
                eye = DrawHead(player);
                if (float.IsNaN(eye.X) || float.IsNaN(eye.Y)) return;
                using (var state = new GraphicsScope(gd))
                {
                    EnsureSceneTarget();
                    if (now >= nextRefresh || previousSub != player.Submarine || mapCount != MapEntity.MapEntityList.Count)
                    RefreshScene(player);
                    view = Matrix.CreateLookAt(Vector3.Zero, Forward(yaw, pitch), Vector3.Up);
                    projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(fov),
                        (float)scene.Width / scene.Height, xrayView ? 7f : 3f, farPlane);
                    sceneFrustum.Matrix = view * projection; frustumReady = true;
                    TrimCache();
                    PrepareStaticImages(mode);
                    if (WarmupPending) { WarmupPending = false; if (settings.WarmupAll) WarmupAll(); }
                    PrepareCharacters(player);
                    TrimCache();
                    coveredCount = 0;
                    foreach (MapEntity candidate in captureCandidates)
                        if (staticStamps.TryGetValue(candidate, out Stamp readyStamp) && readyStamp.HasImage && readyStamp.OutlineAttempted && readyStamp.Mask != null) coveredCount++;
                    cachePending = Math.Max(0, coverageTotal - coveredCount);
                    gd.SetRenderTarget(scene);
                    gd.Viewport = new Viewport(0, 0, scene.Width, scene.Height);
                    gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(8, 17, 27), 1f, 0);
                    ConfigureEffects(player.InWater);
                    if (!xrayView) DrawRoomWalls();
                    DrawStructures(mode);
                    DrawItems(mode);
                    DrawWires();
                    DrawCharacters(mode);
                    FlushWireBoxes();
                    if (!xrayView) DrawWater();
                    state.RestoreTarget();
                    DrawScreenBatch(() => screenBatch.Draw(scene,
                        new Rectangle(gd.Viewport.X, gd.Viewport.Y, gd.Viewport.Width, gd.Viewport.Height), Color.White), BlendState.Opaque);
                }
                frameTimer.Stop(); lastFrameMs = frameTimer.Elapsed.TotalMilliseconds;
            }

            public void InvalidateView() { nextRefresh = 0; }
            public void SetHover(Item item) { hoverItem = item; }
            public void Notify(string text, Color color) { notice = text; noticeColor = color; noticeUntil = now + 2.5; }

            private void EnsureSceneTarget()
            {
                int width = Math.Max(1, GameMain.GraphicsWidth), height = Math.Max(1, GameMain.GraphicsHeight);
                if (scene != null && !scene.IsDisposed && scene.Width == width && scene.Height == height) return;
                scene?.Dispose();
                // The game's map render targets use DepthFormat.None. Our geometry MUST have its own depth attachment.
                try
                {
                    scene = new RenderTarget2D(gd, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8,
                        0, RenderTargetUsage.DiscardContents);
                }
                catch (Exception firstError)
                {
                    WarnOnce("depth-format", "Depth24Stencil8 unavailable; trying Depth16: " + firstError.Message);
                    scene = new RenderTarget2D(gd, width, height, false, SurfaceFormat.Color, DepthFormat.Depth16,
                        0, RenderTargetUsage.DiscardContents);
                }
            }

            private void ConfigureEffects(bool inWater)
            {
                Vector3 fog = inWater ? new Vector3(0.018f, 0.15f, 0.21f) : new Vector3(0.025f, 0.055f, 0.085f);
                float fogEnd = xrayView ? farPlane : inWater ? 1100f : range;
                solidEffect.View = cutoutEffect.View = view;
                solidEffect.Projection = cutoutEffect.Projection = projection;
                solidEffect.FogEnabled = cutoutEffect.FogEnabled = !xrayView;
                solidEffect.FogColor = cutoutEffect.FogColor = fog;
                solidEffect.FogStart = cutoutEffect.FogStart = xrayView ? range * 0.60f : inWater ? 80f : 900f;
                solidEffect.FogEnd = cutoutEffect.FogEnd = fogEnd;
                solidEffect.Alpha = 1f;
                cutoutEffect.Alpha = 1f;
                cutoutEffect.ReferenceAlpha = AlphaCutoff;
                gd.DepthStencilState = DepthStencilState.Default;
                gd.BlendState = BlendState.Opaque;
                gd.RasterizerState = RasterizerState.CullNone;
                gd.SamplerStates[0] = SamplerState.PointClamp;
            }

            private static Bounds2 EntityBounds(MapEntity entity)
            {
                Rectangle r = entity.WorldRect;
                Vector2 correction = SubDrawCorrection(entity.Submarine);
                Vector2 center = new Vector2(r.X + r.Width * 0.5f, r.Y - r.Height * 0.5f) + correction;
                float w = Math.Max(1, r.Width), h = Math.Max(1, r.Height);
                if (entity is Structure structure)
                {
                    float angle = structure.RotationRad;
                    float c = Math.Abs((float)Math.Cos(angle)), s = Math.Abs((float)Math.Sin(angle));
                    float rw = w * c + h * s, rh = h * c + w * s;
                    w = rw; h = rh;
                }
                else if (entity is Item item && item.Sprite != null)
                {
                    // Rotation-aware AABB, not a large square around every narrow ladder.
                    // Doors use their native rectangle and are captured as a separate leaf.
                    if (item.GetComponent<Door>() == null)
                    {
                        float angle = MathHelper.ToRadians(item.Rotation);
                        float c = Math.Abs((float)Math.Cos(angle)), sn = Math.Abs((float)Math.Sin(angle));
                        float rw = w * c + h * sn, rh = h * c + w * sn;
                        w = rw; h = rh;
                    }
                }
                return new Bounds2(center.X - w * 0.5f, center.Y - h * 0.5f,
                    center.X + w * 0.5f, center.Y + h * 0.5f).Expanded(3f);
            }



            private int CompareDistance(MapEntity a, MapEntity b)
            {
                int order = EntityBounds(a).DistanceSquared(eye).CompareTo(EntityBounds(b).DistanceSquared(eye));
                return order != 0 ? order : a.ID.CompareTo(b.ID);
            }


            private float ReadDepth(MapEntity entity)
            {
                // GetDrawDepth contains batching/ID micro-offsets. They are useful for
                // draw order, not as a physical distance between two pieces of furniture.
                if (entity is Item item) return SafeDepth(item.SpriteDepth);
                try
                {
                    if (entity is Structure && realStructureDepth != null &&
                        realStructureDepth.Invoke(entity, null) is float depth) return SafeDepth(depth);
                }
                catch (Exception ex) { WarnOnce("depth-read", "Editor layer fallback: " + ex.Message); }
                if (entity is Structure structure && structure.Prefab.Sprite != null)
                return SafeDepth(structure.Prefab.Sprite.Depth);
                return 0.5f;
            }

            private static readonly MethodInfo realStructureDepth = typeof(Structure).GetMethod(
                "GetRealDepth", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);

            private static float SafeDepth(float value)
            {
                return float.IsNaN(value) || float.IsInfinity(value) ? 0.5f : MathHelper.Clamp(value, 0f, 1f);
            }





            private readonly IdentityMap identity = new IdentityMap();
            private int clampedPlacements;
            private float CorridorDepth(Submarine submarine) { return halfDepth; }
            private static bool BankEnabled(LayerPlacement layer, int bank)
            { return (layer.Banks & (bank > 0 ? 1 : 2)) != 0; }
            public bool surfaceTexture = true;
            public float contourRefreshScale = 1f;
            public void ApplySettings(Settings value)
            {
                settings = value; halfDepth = value.RoomHalfDepth;
                surfaceTexture = value.SurfaceTexture;
                contourRefreshScale = value.ContourRefreshScale;
                identity.Clear(); nextRefresh = 0;
            }
            private void BuildLayers(List<MapEntity> entities)
            {
                layers.Clear(); clampedPlacements = 0;
                foreach (MapEntity entity in entities)
                {
                    EntityKey key = identity.Get(entity);
                    ObjectRule rule = settings.Resolve(key);
                    float native = ReadDepth(entity);
                    Rectangle rect = entity.Rect;
                    bool roomShell = entity is Structure structure && structure.HasBody || entity is Item doorItem && doorItem.GetComponent<Door>() != null;
                    float automatic = entity is Structure ? MathHelper.Clamp(Math.Min(rect.Width, rect.Height) * 0.16f, 4f, 16f)
                        : MathHelper.Clamp(Math.Min(rect.Width, rect.Height) * 0.44f, 20f, 72f);
                    if (entity is Item wireItem && wireItem.GetComponent<Wire>() != null) automatic = 4f;
                    float requestedDepth = (rule.Depth ?? automatic) * settings.DepthScale * (rule.Scale ?? 1f);
                    float depth = MathHelper.Clamp(requestedDepth, 0f, halfDepth - 13f);
                    // Deterministic placement: no collision packing, no nearest-item ranks,
                    // no room-specific expansion. Lower native depth puts the front face inward.
                    float requestedFront = halfDepth - settings.SurfaceInset -
                        (1f - native) * settings.LayerSpread * settings.DepthScale - (rule.Offset ?? 0f) + (entity.ID % 997) * 0.0001f;
                    float front = MathHelper.Clamp(requestedFront, 12f, halfDepth - depth - 1f);
                    bool clamped = !roomShell && (Math.Abs(depth - requestedDepth) > 0.001f || Math.Abs(front - requestedFront) > 0.001f);
                    if (clamped) clampedPlacements++;
                    int banks = rule.Side == "positive" ? 1 : rule.Side == "negative" ? 2 : rule.Side == "hide" ? 0 : 3;
                    layers[entity] = new LayerPlacement { Entity = entity, Bounds = EntityBounds(entity), Native = native,
                        Z = front + depth * 0.5f, HalfDepth = depth * 0.5f, Banks = banks, Key = key,
                        Rule = rule, RoomShell = roomShell, Clamped = clamped, BaseDepth = rule.Depth ?? automatic };
                }
            }

            private void RefreshScene(Character player)
            {
                structures.Clear(); items.Clear(); hulls.Clear();
                var all = new List<MapEntity>();
                float rangeSq = range * range, farthestSq = rangeSq;
                foreach (MapEntity entity in MapEntity.MapEntityList)
                {
                    if (entity == null || entity.Removed) continue;
                    if (entity is Item contained && contained.ParentInventory != null) continue;
                    if (!(entity is Item) && !(entity is Structure)) continue;
                    all.Add(entity);
                    Bounds2 b = EntityBounds(entity);
                    float distanceSq = b.DistanceSquared(eye);
                    if (!fullView && distanceSq > rangeSq) continue;
                    if (entity is Item item) items.Add(item);
                    else structures.Add((Structure)entity);
                    float x = Math.Max(Math.Abs(b.Left - eye.X), Math.Abs(b.Right - eye.X));
                    float y = Math.Max(Math.Abs(b.Bottom - eye.Y), Math.Abs(b.Top - eye.Y));
                    farthestSq = Math.Max(farthestSq, x * x + y * y);
                }
                BuildLayers(all);
                structures.Sort((a, b) => CompareDistance(a, b));
                items.Sort((a, b) => CompareDistance(a, b));
                foreach (Hull hull in Hull.HullList)
                    if (hull != null && !hull.Removed && hull.Submarine != null &&
                        (fullView || EntityBounds(hull).DistanceSquared(eye) <= rangeSq)) hulls.Add(hull);
                shellDepths.Clear(); backWallDepths.Clear();
                foreach (Structure structure in structures)
                {
                    if (!structure.HasBody) continue;
                    // Tiny fixed draw separation only, never a cumulative width increase.
                    shellDepths[structure] = halfDepth + 1f + (structure.ID % 97) * 0.001f;
                    if (structure.Submarine != null) backWallDepths[structure.Submarine] = halfDepth + 2f;
                }
                RefreshWires();
                BuildBackdrop();
                if (fullView) expandedFarPlane = farPlane = Math.Max(range + 1000f, (float)Math.Sqrt(farthestSq) + 1500f);
                nextRefresh = now + 0.6;
                previousSub = player.Submarine; mapCount = MapEntity.MapEntityList.Count;
            }

            private static bool Overlap(Bounds2 a, Bounds2 b)
            {
                return a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;
            }



            private LayerPlacement Placement(MapEntity entity)
            {
                if (layers.TryGetValue(entity, out LayerPlacement layer)) return layer;
                return fallbackLayer;
            }

            private float ShellDepth(Structure structure)
            {
                return shellDepths.TryGetValue(structure, out float depth) ? depth : halfDepth + 1f;
            }

            private void BuildBackdrop()
            {
                backdrop.Clear();
                var bySub = new Dictionary<Submarine, List<Bounds2>>();
                foreach (Hull hull in hulls)
                {
                    if (!bySub.TryGetValue(hull.Submarine, out List<Bounds2> list))
                    { list = new List<Bounds2>(); bySub[hull.Submarine] = list; }
                    Rectangle r = hull.WorldRect;
                    Vector2 p = hull.Submarine.Position;
                    list.Add(new Bounds2(r.X - p.X, r.Y - r.Height - p.Y, r.Right - p.X, r.Y - p.Y));
                }
                foreach (var pair in bySub)
                {
                    var ys = new SortedSet<float>();
                    foreach (Bounds2 b in pair.Value) { ys.Add(b.Bottom); ys.Add(b.Top); }
                    var rows = new List<float>(ys);
                    for (int row = 0; row + 1 < rows.Count; row++)
                    {
                        float bottom = rows[row], top = rows[row + 1], mid = (bottom + top) * 0.5f;
                        var spans = new List<Bounds2>();
                        foreach (Bounds2 b in pair.Value) if (b.Bottom < mid && b.Top > mid) spans.Add(b);
                        spans.Sort((a, b) => a.Left.CompareTo(b.Left));
                        float left = 0f, right = 0f; bool any = false;
                        foreach (Bounds2 b in spans)
                        {
                            if (!any) { left = b.Left; right = b.Right; any = true; }
                            else if (b.Left <= right + 0.01f) right = Math.Max(right, b.Right);
                            else
                            {
                                backdrop.Add(new BackdropTile { Sub = pair.Key, Local = new Bounds2(left, bottom, right, top) });
                                left = b.Left; right = b.Right;
                            }
                        }
                        if (any) backdrop.Add(new BackdropTile { Sub = pair.Key, Local = new Bounds2(left, bottom, right, top) });
                    }
                }
            }

            private int Revision(MapEntity entity)
            {
                unchecked
                {
                    Rectangle r = entity.Rect;
                    int value = r.Width * 397 ^ r.Height;
                    if (entity is Structure s)
                    {
                        value = value * 397 ^ s.RotationRad.GetHashCode();
                        value = value * 397 ^ s.FlippedX.GetHashCode();
                        value = value * 397 ^ s.FlippedY.GetHashCode();
                        value = value * 397 ^ s.Scale.GetHashCode();
                        value = value * 397 ^ s.SpriteColor.GetHashCode();
                        if (s.Sections != null)
                        foreach (var section in s.Sections) value = value * 31 ^ (int)section.damage;
                    }
                    else if (entity is Item item)
                    {
                        value = value * 397 ^ item.Rotation.GetHashCode();
                        value = value * 397 ^ item.SpriteColor.GetHashCode();
                        value = value * 397 ^ item.SpriteDepth.GetHashCode();
                        Door door = item.GetComponent<Door>();
                        if (door != null) value = value * 397 ^ (int)(MathHelper.Clamp(door.OpenState, 0f, 1f) * 64f);

                    }
                    return value;
                }
            }

            private Stamp GetStamp(MapEntity entity)
            {
                if (!staticStamps.TryGetValue(entity, out Stamp stamp))
                {
                    stamp = new Stamp();
                    staticStamps.Add(entity, stamp);
                }
                stamp.Bounds = EntityBounds(entity);
                stamp.LastSeen = now;
                return stamp;
            }

            private int captureCursor;
            private readonly List<MapEntity> captureCandidates = new List<MapEntity>();
            private readonly HashSet<MapEntity> captureSet = new HashSet<MapEntity>();

            private void AddCaptureCandidates<T>(List<T> list, int limit) where T : MapEntity
            {
                int added = 0;
                for (int pass = 0; pass < 2; pass++)
                {
                    int allowance = pass == 0 ? Math.Max(1, limit * 3 / 4) : limit;
                    foreach (T entity in list)
                    {
                        if (added >= allowance) break;
                        if (!CaptureEligible(entity) || captureSet.Contains(entity)) continue;
                        if (entity is Item item && item.ParentInventory != null) continue;
                        if (pass == 0)
                        {
                            Bounds2 b = EntityBounds(entity);
                            bool shell = entity is Structure st && st.HasBody;
                            bool door = entity is Item it && it.GetComponent<Door>() != null;
                            LayerPlacement p = Placement(entity);
                            bool visible = shell || door ? Visible(b, 0f, CorridorDepth(entity.Submarine))
                            : Visible(b, p.Z, p.HalfDepth) || Visible(b, -p.Z, p.HalfDepth);
                            if (!visible) continue;
                        }
                        captureCandidates.Add(entity); captureSet.Add(entity); added++;
                    }
                }
            }

            private readonly List<MapEntity> detailCandidates = new List<MapEntity>();
            private int fillCursor, maintenanceCursor;
            private int coveredCount, coverageTotal, budgetBlocked;
            private int requestedCaptureSize;
            private bool WithinBudget(int jobs, Stopwatch timer)
            { return jobs < settings.JobsPerFrame && (jobs == 0 || timer.Elapsed.TotalMilliseconds < settings.CaptureBudgetMs); }
            private bool CaptureEligible(MapEntity entity)
            {
                if (entity == null || entity.Removed || Placement(entity).Banks == 0) return false;
                return !(entity is Item item) || item.ParentInventory == null && item.GetComponent<Wire>() == null;
            }
            private void PrepareStaticImages(RenderMode mode)
            {
                int jobs = 0; budgetBlocked = 0;
                Stopwatch timer = Stopwatch.StartNew();
                captureCandidates.Clear(); captureSet.Clear(); detailCandidates.Clear();
                if (CaptureEligible(hoverItem))
                { captureCandidates.Add(hoverItem); captureSet.Add(hoverItem); }
                AddCaptureCandidates(items, MaxItems - captureCandidates.Count);
                AddCaptureCandidates(structures, MaxStructures);
                detailCandidates.AddRange(captureCandidates);
                // FULL does not stop at the near-detail pool. Every loaded candidate gets
                // a persistent low-resolution contour; it is not replaced by sliced LOD.
                if (fullView)
                {
                    foreach (Item item in items)
                        if (CaptureEligible(item) && captureSet.Add(item)) captureCandidates.Add(item);
                    foreach (Structure structure in structures)
                        if (CaptureEligible(structure) && captureSet.Add(structure)) captureCandidates.Add(structure);
                }
                foreach (MapEntity entity in captureCandidates) GetStamp(entity);
                if (CaptureEligible(hoverItem))
                    PrepareEntity(hoverItem, true, mode, settings.DetailTextureSize, ref jobs, timer);
                // Reserve a share for unseen rooms BEFORE servicing ongoing animations.
                int total = captureCandidates.Count, fillLimit = Math.Max(1, settings.JobsPerFrame - 1);
                for (int n = 0; n < total && jobs < fillLimit && WithinBudget(jobs, timer); n++)
                {
                    if (fillCursor >= total) fillCursor = 0;
                    MapEntity entity = captureCandidates[fillCursor++];
                    Stamp stamp = staticStamps[entity];
                    if (!stamp.HasImage || !stamp.OutlineAttempted)
                        PrepareEntity(entity, entity is Item, mode, settings.FullTextureSize, ref jobs, timer);
                    if (budgetBlocked > 0) break;
                }
                int detailed = detailCandidates.Count;
                for (int n = 0; n < detailed && WithinBudget(jobs, timer); n++)
                {
                    if (captureCursor >= detailed) captureCursor = 0;
                    MapEntity entity = detailCandidates[captureCursor++];
                    PrepareEntity(entity, entity is Item, mode, settings.DetailTextureSize, ref jobs, timer);
                }
                // Changed remote poses are eventually refreshed too; no current-Hull gate.
                for (int n = 0; n < Math.Min(total, 128) && WithinBudget(jobs, timer); n++)
                {
                    if (maintenanceCursor >= total) maintenanceCursor = 0;
                    MapEntity entity = captureCandidates[maintenanceCursor++];
                    PrepareEntity(entity, false, mode, settings.FullTextureSize, ref jobs, timer);
                }
                coveredCount = 0; coverageTotal = captureCandidates.Count;
                foreach (MapEntity entity in captureCandidates)
                    if (staticStamps.TryGetValue(entity, out Stamp stamp) && stamp.HasImage && stamp.OutlineAttempted && stamp.Mask != null) coveredCount++;
                cachePending = Math.Max(0, coverageTotal - coveredCount);
            }

            /// <summary>
            /// One synchronous capture pass over every candidate so the whole
            /// submarine is volumetric immediately instead of warming up over
            /// the next seconds. Small textures are used here; the normal
            /// budgeted passes later upgrade nearby objects to full detail.
            /// </summary>
            private void WarmupAll()
            {
                int savedJobs = settings.JobsPerFrame;
                double savedBudget = settings.CaptureBudgetMs;
                settings.JobsPerFrame = int.MaxValue;
                settings.CaptureBudgetMs = double.MaxValue;
                try
                {
                    // Warm EVERY eligible loaded object from the FULL entity
                    // list, not the distance-culled per-frame pools, so all
                    // rooms are volumetric even in Normal view.
                    var targets = new List<MapEntity>();
                    foreach (MapEntity entity in MapEntity.MapEntityList)
                    {
                        if (entity == null || entity.Removed) continue;
                        bool eligible = (entity is Item it && it.ParentInventory == null)
                                     || entity is Structure;
                        if (eligible && CaptureEligible(entity)) targets.Add(entity);
                    }

                    int warmed = 0;
                    foreach (MapEntity entity in targets)
                    {
                        Stamp stamp = GetStamp(entity);
                        if (stamp == null || !stamp.Bounds.Valid || stamp.HasImage) continue;
                        // no TrimCache during warmup: it would evict just-captured stamps
                        int jobs = 0; Stopwatch timer = Stopwatch.StartNew();
                        PrepareEntity(entity, entity is Item, RenderMode.Contours,
                            WarmupTextureSize > 0 ? WarmupTextureSize : settings.FullTextureSize, ref jobs, timer);
                        warmed++;
                    }
                    Log("Warmup: " + warmed + " objects now volumetric", Color.LimeGreen);
                }
                finally { settings.JobsPerFrame = savedJobs; settings.CaptureBudgetMs = savedBudget; }
            }

            private void PrepareEntity(MapEntity entity, bool dynamicImage, RenderMode mode, int quality, ref int jobs, Stopwatch timer)
            {
                Stamp stamp = GetStamp(entity);
                if (!stamp.Bounds.Valid || now < stamp.RetryAfter) return;
                int revision = Revision(entity);
                float distanceSq = stamp.Bounds.DistanceSquared(eye);
                bool isDoor = entity is Item doorItem && doorItem.GetComponent<Door>() != null;
                double interval = isDoor
                    ? 0.0
                    : distanceSq < 300f * 300f ? 0.18 : distanceSq < 800f * 800f ? 0.5 : 8.0;
                interval /= MathF.Max(0.1f, contourRefreshScale);
                bool upgrade = stamp.HasImage && stamp.Quality < quality;
                bool dirty = !stamp.HasImage || revision != stamp.ShapeRevision || upgrade ||
                    (dynamicImage && (isDoor || now - stamp.LastCapture > interval));
                if (!dirty && stamp.OutlineAttempted) return;
                if (!WithinBudget(jobs, timer)) return;
                jobs++; long oldBytes = stamp.Bytes;
                try
                {
                    if (dirty)
                    {
                        // Never downgrade a retained image simply because the player left a room.
                        requestedCaptureSize = Math.Max(quality, stamp.Quality);
                        if (!CaptureEntity(entity, stamp)) { budgetBlocked++; return; }
                        stamp.Quality = requestedCaptureSize; stamp.ShapeRevision = revision; stamp.LastCapture = now;
                        stamp.OutlineAttempted = false; stamp.Mask = null;
                        stamp.Sides?.Dispose(); stamp.Sides = null;
                    }
                    if (!stamp.OutlineAttempted)
                    {
                        stamp.Sides = BuildOutline(stamp);
                        stamp.OutlineAttempted = true;
                        // Do not silently discard the contour and call a flat card "ready".
                        // A too-small user budget is shown explicitly; TrimCache keeps the cap.
                        if (cacheBytes - oldBytes + stamp.Bytes > MaxCacheBytes) budgetBlocked++;
                    }
                    stamp.Failed = false;
                }
                catch (Exception ex)
                {
                    stamp.Failed = true; stamp.RetryAfter = now + 5.0; stamp.OutlineAttempted = false;
                    ResetCaptureBatch();
                    WarnOnce("capture-" + entity.GetType().Name, "Capture retry scheduled: " + ex.GetBaseException().Message);
                }
                finally { cacheBytes += stamp.Bytes - oldBytes; }
            }



            private void ResetCaptureBatch()
            {
                captureBatch.Dispose();
                captureBatch = new SpriteBatch(gd);
            }

            private bool EnsureStampTexture(Stamp stamp, int maxSide, bool budgetLimited)
            {
                Bounds2 b = stamp.Bounds;
                float scale = Math.Min(1f, maxSide / Math.Max(b.Width, b.Height));
                // Character RTs have a fixed square allocation. CaptureTransform stretches
                // each axis and DrawCard undoes that stretch: no texture realloc per pose.
                int width = budgetLimited ? Math.Max(4, (int)Math.Ceiling(b.Width * scale)) : maxSide;
                int height = budgetLimited ? Math.Max(4, (int)Math.Ceiling(b.Height * scale)) : maxSide;
                if (stamp.Texture != null && !stamp.Texture.IsDisposed && stamp.Texture.Width == width && stamp.Texture.Height == height) return true;
                if (budgetLimited && cacheBytes - stamp.Bytes + (long)width * height * 4 > MaxCacheBytes) return false;
                stamp.Dispose();
                stamp.Texture = new RenderTarget2D(gd, width, height, false, SurfaceFormat.Color, DepthFormat.None,
                    0, RenderTargetUsage.PreserveContents);
                stamp.OutlineAttempted = false;
                return true;
            }

            private Matrix CaptureTransform(Stamp stamp)
            {
                Bounds2 b = stamp.Bounds;
                return Matrix.CreateTranslation(-b.Left, b.Top, 0f) *
                Matrix.CreateScale(stamp.Texture.Width / b.Width, stamp.Texture.Height / b.Height, 1f);
            }

            private static readonly MethodInfo itemDrawWithTint = typeof(Item).GetMethod("Draw",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(SpriteBatch), typeof(bool), typeof(bool), typeof(Color?), typeof(float?) }, null);

            private bool CaptureEntity(MapEntity entity, Stamp stamp)
            {
                if (!EnsureStampTexture(stamp, requestedCaptureSize, true)) return false;
                using (var state = new GraphicsScope(gd))
                {
                    gd.SetRenderTarget(stamp.Texture);
                    gd.Clear(Color.Transparent);
                    bool begun = false;
                    try
                    {
                        captureBatch.Begin(SpriteSortMode.BackToFront, captureBlend, SamplerState.LinearClamp,
                            DepthStencilState.None, RasterizerState.CullNone, null, CaptureTransform(stamp));
                        begun = true;
                        if (entity is Item doorItem && doorItem.GetComponent<Door>() is Door door)
                        {
                            // The Item base sprite is often only a placeholder/frame. Extruding
                            // it produced the two white plates. Capture ONLY the native door leaf.
                            if (door.OpenState < 0.999f)
                            door.Draw(captureBatch, false, -1f, Color.White);
                            stamp.CapturedDoorState = door.OpenState;
                        }
                        else if (entity is Item item && itemDrawWithTint != null)
                        {
                            // Do not bake the native 2D cursor highlight into a 3D texture.
                            itemDrawWithTint.Invoke(item, new object[] { captureBatch, false, true, (Color?)item.SpriteColor, null });
                            itemDrawWithTint.Invoke(item, new object[] { captureBatch, false, false, (Color?)item.SpriteColor, null });
                        }
                        else
                        {
                            entity.Draw(captureBatch, false, true);
                            entity.Draw(captureBatch, false, false);
                        }
                    }
                    finally { if (begun) captureBatch.End(); }
                }
                stamp.ImageBounds = stamp.Bounds;
                stamp.HasImage = true;
                // One-time pixel readback: is the captured RT actually empty?
                if (entity is Item diagItem && diagItem.GetComponent<Door>() != null && !stamp.DiagLogged)
                {
                    stamp.DiagLogged = true;
                    try
                    {
                        var px = new Color[stamp.Texture.Width * stamp.Texture.Height];
                        stamp.Texture.GetData(px);
                        int opaque = 0, total = px.Length;
                        long r = 0, g = 0, b = 0;
                        foreach (var c in px) { if (c.A > 128) { opaque++; r += c.R; g += c.G; b += c.B; } }
                        string msg = opaque > 0
                            ? $"capture OK: {opaque}/{total} opaque px, avg ({r / opaque},{g / opaque},{b / opaque})"
                            : "capture EMPTY: 0 opaque pixels!";
                        Log("door stamp diag [" + diagItem.Prefab?.Identifier + "]: " + msg,
                            opaque > 0 ? Color.LimeGreen : Color.Red);
                    }
                    catch (Exception ex) { Log("door stamp diag failed: " + ex.Message, Color.Orange); }
                }
                return true;
            }

            private static bool CharacterBounds(Character character, out Bounds2 bounds)
            {
                bounds = new Bounds2(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                bool found = false;
                if (character.AnimController?.Limbs == null) return false;
                foreach (Limb limb in character.AnimController.Limbs)
                {
                    if (limb?.body == null) continue;
                    Sprite sprite = limb.ActiveSprite;
                    if (sprite?.Texture == null || sprite.Texture.IsDisposed) continue;
                    Rectangle source = sprite.SourceRect;
                    float scale = Math.Abs(limb.Scale * limb.TextureScale);
                    float rx = Math.Max(Math.Abs(sprite.Origin.X), Math.Abs(source.Width - sprite.Origin.X));
                    float ry = Math.Max(Math.Abs(sprite.Origin.Y), Math.Abs(source.Height - sprite.Origin.Y));
                    float radius = (float)Math.Sqrt(rx * rx + ry * ry) * scale + 18f;
                    Vector2 p = limb.body.DrawPosition;
                    bounds.Left = Math.Min(bounds.Left, p.X - radius);
                    bounds.Right = Math.Max(bounds.Right, p.X + radius);
                    bounds.Bottom = Math.Min(bounds.Bottom, p.Y - radius);
                    bounds.Top = Math.Max(bounds.Top, p.Y + radius);
                    found = true;
                }
                return found && bounds.Valid && bounds.Width < 5000f && bounds.Height < 5000f;
            }

            private void PrepareCharacters(Character player)
            {
                characters.Clear();
                foreach (Character character in Character.CharacterList)
                {
                    if (character == null || character.Removed || ReferenceEquals(character, player)) continue;
                    if (Vector2.DistanceSquared(character.WorldPosition, player.WorldPosition) < range * range)
                    characters.Add(character);
                }
                characters.Sort((a, b) => Vector2.DistanceSquared(a.WorldPosition, player.WorldPosition)
                    .CompareTo(Vector2.DistanceSquared(b.WorldPosition, player.WorldPosition)));
                if (characters.Count > MaxCharacters) characters.RemoveRange(MaxCharacters, characters.Count - MaxCharacters);
                foreach (Character character in characters)
                {
                    if (!CharacterBounds(character, out Bounds2 b)) continue;
                    if (!characterStamps.TryGetValue(character, out Stamp stamp))
                    { stamp = new Stamp(); characterStamps.Add(character, stamp); }
                    stamp.Bounds = b; stamp.LastSeen = now;
                    if (stamp.Failed) continue;
                    try
                    {
                        // Quantize dimensions to reduce reallocations while an arm/leg animates.
                        Vector2 center = b.Center;
                        float width = (float)Math.Ceiling(b.Width / 32f) * 32f;
                        float height = (float)Math.Ceiling(b.Height / 32f) * 32f;
                        stamp.Bounds = new Bounds2(center.X - width / 2, center.Y - height / 2,
                            center.X + width / 2, center.Y + height / 2);
                        EnsureStampTexture(stamp, 320, false);
                        CaptureCharacter(character, stamp);
                    }
                    catch (Exception ex)
                    {
                        stamp.Failed = true;
                        ResetCaptureBatch();
                        WarnOnce("character-capture", "Character capture failed: " + ex);
                    }
                }
            }

            private void CaptureCharacter(Character character, Stamp stamp)
            {
                using (var state = new GraphicsScope(gd))
                {
                    gd.SetRenderTarget(stamp.Texture); gd.Clear(Color.Transparent);
                    captureCamera.SetResolution(new Point(stamp.Texture.Width, stamp.Texture.Height));
                    captureCamera.Position = stamp.Bounds.Center;
                    captureCamera.MinZoom = 0.001f;
                    captureCamera.Zoom = stamp.Texture.Width / stamp.Bounds.Width;
                    captureCamera.UpdateTransform(false, false); // do NOT move the audio listener
                    bool begun = false;
                    try
                    {
                        captureBatch.Begin(SpriteSortMode.BackToFront, captureBlend, SamplerState.LinearClamp,
                            DepthStencilState.None, RasterizerState.CullNone, null, CaptureTransform(stamp));
                        begun = true;
                        foreach (Limb limb in character.AnimController.Limbs)
                        {
                            if (limb?.body == null) continue;
                            // Native limb draw includes clothing/conditional sprites. Disable GPU
                            // deformations for this offscreen pass only; do not modify limb state.
                            limb.Draw(captureBatch, captureCamera, null, true);
                        }
                    }
                    finally { if (begun) captureBatch.End(); }
                }
                stamp.ImageBounds = stamp.Bounds;
                stamp.HasImage = true; stamp.LastCapture = now;
            }

            private void TrimCache()
            {
                cacheBytes = 0;
                removals.Clear();
                foreach (var pair in staticStamps)
                {
                    if (pair.Key.Removed) { removals.Add(pair.Key); continue; }
                    // Never time-evict stamps near the eye — they're being drawn.
                    if (!fullView)
                    {
                        var b = EntityBounds(pair.Key);
                        if (b.DistanceSquared(eye) < range * range * 0.25f)
                        { cacheBytes += pair.Value.Bytes; continue; }
                    }
                    if (!fullView && now - pair.Value.LastSeen > 30.0) removals.Add(pair.Key);
                    else cacheBytes += pair.Value.Bytes;
                }
                foreach (MapEntity entity in removals) { staticStamps[entity].Dispose(); staticStamps.Remove(entity); }
                while (staticStamps.Count > MaxStaticEntries || cacheBytes > MaxCacheBytes)
                {
                    MapEntity oldest = null; double age = double.MaxValue;
                    foreach (var pair in staticStamps)
                    {
                        if (cacheBytes > MaxCacheBytes && pair.Value.Bytes == 0) continue;
                        double priority = pair.Value.LastDrawn + pair.Value.LastSeen * 0.00001;
                        if (priority < age) { oldest = pair.Key; age = priority; }
                    }
                    if (oldest == null) break;
                    cacheBytes -= staticStamps[oldest].Bytes;
                    staticStamps[oldest].Dispose(); staticStamps.Remove(oldest);
                }
                characterRemovals.Clear();
                foreach (var pair in characterStamps)
                if (pair.Key.Removed || now - pair.Value.LastSeen > 1.0) characterRemovals.Add(pair.Key);
                foreach (Character character in characterRemovals)
                { characterStamps[character].Dispose(); characterStamps.Remove(character); }
            }

            /// <summary>
            /// Original texture of the nearest captured horizontal structure —
            /// used for the room shell so stretched floors/ceilings keep the
            /// authored look instead of the procedural metal.
            /// </summary>
            private Texture2D PickSurfaceTexture(Bounds2 tile)
            {
                // Original authored texture of the nearest horizontal wall —
                // available immediately from prefabs, no stamp capture needed,
                // and it never stretches: UVs wrap per 96-unit tile.
                Structure best = null; float bestSq = float.MaxValue;
                Vector2 center = new Vector2((tile.Left + tile.Right) * 0.5f, (tile.Bottom + tile.Top) * 0.5f) + eye;
                foreach (Structure structure in structures)
                {
                    if (structure == null || structure.Removed) continue;
                    if (structure.Prefab?.Sprite?.Texture == null) continue;
                    float d = Vector2.DistanceSquared(structure.WorldPosition, center);
                    if (d < bestSq) { bestSq = d; best = structure; }
                }
                var sprite = best?.Prefab?.Sprite;
                var texture = sprite?.Texture;
                return texture != null && !texture.IsDisposed ? texture : null;
            }

            private void DrawRoomWalls()
            {
                // Union tiles do not overlap. Their UVs are submarine-local, not per-hull.
                // This prevents coplanar backdrops and texture phase jumps at hull borders.

                foreach (BackdropTile tile in backdrop)
                {
                    if (tile.Sub.Removed) continue;
                    Bounds2 b = tile.Local;
                    Vector2 offset = tile.Sub.DrawPosition - eye;
                    float l = b.Left + offset.X, r = b.Right + offset.X;
                    float t = b.Top + offset.Y, bottom = b.Bottom + offset.Y;
                    float wallZ = halfDepth + 2f;
                    // Only the FAR copy (behind the eye). The near copy z-fights
                    // with item cards and flickers on ceiling/wall seams.
                    float z = wallZ;
                    Color tint = new Color(126, 151, 169);
                    Texture2D surfaceTex = surfaceTexture ? PickSurfaceTexture(b) : null;
                    if (surfaceTex == null) surfaceTex = metal;
                    DrawQuad(surfaceTex, new Vector3(l, t, -z), new Vector3(r, t, -z),
                        new Vector3(l, bottom, -z), new Vector3(r, bottom, -z),
                        b.Left / 96f, -b.Top / 96f, b.Right / 96f, -b.Bottom / 96f,
                        tint, false, Matrix.Identity, true);
                }
            }

            private readonly BoundingFrustum sceneFrustum = new BoundingFrustum(Matrix.Identity);
            private bool frustumReady;

            private bool Visible(Bounds2 b, float z, float depth)
            {
                if (!frustumReady) return true;
                Vector2 center = b.Center - eye;
                float radius = (float)Math.Sqrt(b.Width * b.Width + b.Height * b.Height) * 0.5f + depth;
                return sceneFrustum.Contains(new BoundingSphere(new Vector3(center.X, center.Y, z), radius))
                != ContainmentType.Disjoint;
            }

            private Bounds2 RenderBounds(MapEntity entity, Stamp stamp)
            {
                Bounds2 live = EntityBounds(entity);
                if (stamp?.HasImage != true || !stamp.ImageBounds.Valid) return live;
                // Moving the submarine translates the cached image, but a newly measured
                // AABB must not stretch a texture captured at a different angle.
                Vector2 center = live.Center;
                return new Bounds2(center.X - stamp.ImageBounds.Width * 0.5f, center.Y - stamp.ImageBounds.Height * 0.5f,
                    center.X + stamp.ImageBounds.Width * 0.5f, center.Y + stamp.ImageBounds.Height * 0.5f);
            }

            private sealed class WireVisual
            {
                public Item Item; public Wire Component; public Sprite Sprite;
                public List<Wire.WireSection> Sections;
            }
            private static readonly FieldInfo wireSectionsField = typeof(Wire).GetField("sections", BindingFlags.NonPublic | BindingFlags.Instance);
            private static readonly FieldInfo wireSpriteField = typeof(Wire).GetField("wireSprite", BindingFlags.NonPublic | BindingFlags.Instance);
            private static readonly MethodInfo wireOffsetMethod = typeof(Wire).GetMethod("GetDrawOffset", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            private readonly Dictionary<Item, WireVisual> wireLookup = new Dictionary<Item, WireVisual>();
            private readonly Dictionary<Texture2D, List<WireVisual>> wireGroups = new Dictionary<Texture2D, List<WireVisual>>();
            private readonly VertexPositionColorTexture[] cableVertices = new VertexPositionColorTexture[24000];
            private readonly short[] cableIndices = new short[36000];
            private int cableVertexCount;
            private Texture2D cableTexture;

            private void RefreshWires()
            {
                wireLookup.Clear(); wireGroups.Clear();
                foreach (Item item in items)
                {
                    Wire wire = item.GetComponent<Wire>();
                    if (wire == null || Placement(item).Banks == 0) continue;
                    try
                    {
                        var sections = wireSectionsField?.GetValue(wire) as List<Wire.WireSection>;
                        if (sections == null)
                        {
                            List<Vector2> nodes = wire.GetNodes();
                            sections = new List<Wire.WireSection>();
                            for (int i = 1; i < nodes.Count; i++) sections.Add(new Wire.WireSection(nodes[i - 1], nodes[i]));
                        }
                        Sprite sprite = wireSpriteField?.GetValue(wire) as Sprite;
                        Texture2D texture = sprite?.Texture;
                        if (texture == null || texture.IsDisposed) texture = white;
                        var visual = new WireVisual { Item = item, Component = wire, Sprite = sprite, Sections = sections };
                        wireLookup[item] = visual;
                        if (!wireGroups.TryGetValue(texture, out List<WireVisual> group))
                        { group = new List<WireVisual>(); wireGroups[texture] = group; }
                        group.Add(visual);
                    }
                    catch (Exception ex) { WarnOnce("wire-shape", "Wire geometry unavailable: " + ex.GetBaseException().Message); }
                }
                for (int q = 0; q < cableIndices.Length / 6; q++)
                {
                    short v = (short)(q * 4); int i = q * 6;
                    cableIndices[i] = v; cableIndices[i + 1] = (short)(v + 1); cableIndices[i + 2] = (short)(v + 2);
                    cableIndices[i + 3] = (short)(v + 2); cableIndices[i + 4] = (short)(v + 1); cableIndices[i + 5] = (short)(v + 3);
                }
            }
            private static Vector2 WireOffset(WireVisual wire)
            {
                if (wireOffsetMethod != null && wireOffsetMethod.Invoke(wire.Component, null) is Vector2 offset) return offset;
                Submarine sub = wire.Item.Submarine;
                return sub == null ? Vector2.Zero : sub.DrawPosition + sub.HiddenSubPosition;
            }
            private static float WireWidth(WireVisual wire)
            { return Math.Max(0.1f, wire.Component.Width * (wire.Sprite?.SourceRect.Height ?? 16)); }
            private Bounds2 WireBounds(WireVisual wire, Vector2 offset)
            {
                Bounds2 b = new Bounds2(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
                foreach (Wire.WireSection segment in wire.Sections)
                {
                    b.Left = Math.Min(b.Left, Math.Min(segment.Start.X, segment.End.X) + offset.X);
                    b.Right = Math.Max(b.Right, Math.Max(segment.Start.X, segment.End.X) + offset.X);
                    b.Bottom = Math.Min(b.Bottom, Math.Min(segment.Start.Y, segment.End.Y) + offset.Y);
                    b.Top = Math.Max(b.Top, Math.Max(segment.Start.Y, segment.End.Y) + offset.Y);
                }
                return b.Expanded(WireWidth(wire) * 0.5f);
            }
            private static Color ShadeCable(Color c, float factor)
            { return new Color((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor), c.A); }
            private void CableQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color color, Vector2 uv0, Vector2 uv1)
            {
                if (cableVertexCount + 4 > cableVertices.Length) FlushCables();
                cableVertices[cableVertexCount++] = new VertexPositionColorTexture(a, color, uv0);
                cableVertices[cableVertexCount++] = new VertexPositionColorTexture(b, color, new Vector2(uv1.X, uv0.Y));
                cableVertices[cableVertexCount++] = new VertexPositionColorTexture(c, color, new Vector2(uv0.X, uv1.Y));
                cableVertices[cableVertexCount++] = new VertexPositionColorTexture(d, color, uv1);
            }
            private void FlushCables()
            {
                if (cableVertexCount == 0) return;
                gd.BlendState = BlendState.Opaque; gd.DepthStencilState = DepthStencilState.Default;
                gd.RasterizerState = RasterizerState.CullNone; gd.SamplerStates[0] = SamplerState.LinearClamp;
                cutoutEffect.World = Matrix.Identity; cutoutEffect.Texture = cableTexture;
                foreach (EffectPass pass in cutoutEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, cableVertices, 0, cableVertexCount,
                        cableIndices, 0, cableVertexCount / 2);
                    frameDrawCalls++;
                }
                cableVertexCount = 0;
            }
            private void DrawWires()
            {
                foreach (var group in wireGroups)
                {
                    cableTexture = group.Key; cableVertexCount = 0;
                    if (cableTexture.IsDisposed) continue;
                    foreach (WireVisual wire in group.Value)
                    {
                        if (wire.Item.Removed || wire.Item.ParentInventory != null || wire.Component.Hidden || wire.Sections.Count == 0) continue;
                        LayerPlacement layer = Placement(wire.Item);
                        Vector2 offset = WireOffset(wire);
                        Bounds2 bounds = WireBounds(wire, offset);
                        Rectangle src = wire.Sprite?.SourceRect ?? new Rectangle(0, 0, 1, 1);
                        Vector2 uv0 = new Vector2(src.X / (float)cableTexture.Width, src.Y / (float)cableTexture.Height);
                        Vector2 uv1 = new Vector2(src.Right / (float)cableTexture.Width, src.Bottom / (float)cableTexture.Height);
                        Vector2 centerUv = (uv0 + uv1) * 0.5f;
                        Color color = wire.Item.Color, shade = ShadeCable(color, 0.65f);
                        float halfWidth = WireWidth(wire) * 0.5f;
                        for (int bank = -1; bank <= 1; bank += 2)
                        {
                            if (!BankEnabled(layer, bank) || !Visible(bounds, bank * layer.Z, layer.HalfDepth)) continue;
                            float front = bank * (layer.Z - layer.HalfDepth), back = bank * (layer.Z + layer.HalfDepth);
                            foreach (Wire.WireSection segment in wire.Sections)
                            {
                                Vector2 a = segment.Start + offset - eye, b = segment.End + offset - eye;
                                Vector2 dir = b - a;
                                if (dir.LengthSquared() < 0.0001f) continue;
                                dir.Normalize(); Vector2 n = new Vector2(-dir.Y, dir.X) * halfWidth;
                                Vector3 af = new Vector3(a + n, front), bf = new Vector3(b + n, front);
                                Vector3 cf = new Vector3(a - n, front), df = new Vector3(b - n, front);
                                CableQuad(af, bf, cf, df, color, uv0, uv1);
                                if (layer.HalfDepth <= 0.001f) continue;
                                Vector3 ab = new Vector3(a + n, back), bb = new Vector3(b + n, back);
                                Vector3 cb = new Vector3(a - n, back), db = new Vector3(b - n, back);
                                CableQuad(ab, bb, cb, db, color, uv0, uv1);
                                CableQuad(af, bf, ab, bb, shade, centerUv, centerUv);
                                CableQuad(cf, df, cb, db, shade, centerUv, centerUv);
                                CableQuad(af, cf, ab, cb, shade, centerUv, centerUv);
                                CableQuad(bf, df, bb, db, shade, centerUv, centerUv);
                            }
                        }
                    }
                    FlushCables();
                }
            }
            private static bool CableSlab(float origin, float direction, float min, float max, ref float enter, ref float exit)
            {
                if (Math.Abs(direction) < 0.000001f) return origin >= min && origin <= max;
                float a = (min - origin) / direction, b = (max - origin) / direction;
                if (a > b) { float temp = a; a = b; b = temp; }
                enter = Math.Max(enter, a); exit = Math.Min(exit, b);
                return enter <= exit;
            }
            private bool RayWire(Vector3 ray, WireVisual wire, float limit, out float hit)
            {
                hit = limit;
                if (wire.Component.Hidden || wire.Item.ParentInventory != null || wire.Sections.Count == 0) return false;
                LayerPlacement layer = Placement(wire.Item); bool found = false;
                Vector2 offset = WireOffset(wire) - eye;
                float radius = WireWidth(wire) * 0.5f;
                foreach (Wire.WireSection segment in wire.Sections)
                {
                    Vector2 a = segment.Start + offset, direction = segment.End - segment.Start;
                    float length = direction.Length(); if (length < 0.001f) continue;
                    direction /= length; Vector2 normal = new Vector2(-direction.Y, direction.X);
                    Vector2 rayXY = new Vector2(ray.X, ray.Y);
                    for (int bank = -1; bank <= 1; bank += 2)
                    {
                        if (!BankEnabled(layer, bank)) continue;
                        float enter = 0.001f, exit = hit, z = bank * layer.Z;
                        float depth = Math.Max(0.05f, layer.HalfDepth);
                        if (CableSlab(-Vector2.Dot(a, direction), Vector2.Dot(rayXY, direction), 0f, length, ref enter, ref exit) &&
                            CableSlab(-Vector2.Dot(a, normal), Vector2.Dot(rayXY, normal), -radius, radius, ref enter, ref exit) &&
                            CableSlab(0f, ray.Z, z - depth, z + depth, ref enter, ref exit))
                        { hit = enter; found = true; }
                    }
                }
                return found;
            }

            private static string Csv(string value)
            {
                value = value ?? "";
                if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }
            private static string Number(float value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
            public void ExportObjects()
            {
                try
                {
                    string folder = Path.GetDirectoryName(SettingsStore.ActivePath ?? Path.Combine(SettingsStore.UserFolder, "BaroDepth.xml"));
                    Directory.CreateDirectory(folder);
                    var csv = new StringBuilder("entityId;editorId;prefab;submarine;name;kind;baseDepth;effectiveDepth;frontZ;offset;side;clamped\r\n");
                    var root = new XElement("Objects", new XComment("Templates only. Copy the required Object into BaroDepth.xml and change enabled to true. This file is overwritten by F8."));
                    var ordered = new List<LayerPlacement>(layers.Values);
                    ordered.Sort((a, b) => a.Key.RuntimeId.CompareTo(b.Key.RuntimeId));
                    foreach (LayerPlacement layer in ordered)
                    {
                        MapEntity entity = layer.Entity;
                        if (entity.Removed || entity is Item contained && contained.ParentInventory != null) continue;
                        EntityKey key = layer.Key;
                        csv.Append(key.RuntimeId).Append(';').Append(key.EditorId?.ToString(CultureInfo.InvariantCulture) ?? "").Append(';')
                            .Append(Csv(key.Prefab)).Append(';').Append(Csv(key.Submarine)).Append(';').Append(Csv(entity.Name.ToString())).Append(';')
                            .Append(Csv(layer.RoomShell ? "room-shell" : entity is Item item && item.GetComponent<Wire>() != null ? "wire" : entity.GetType().Name)).Append(';')
                            .Append(Number(layer.BaseDepth)).Append(';').Append(Number(layer.RoomShell ? halfDepth * 2f : layer.HalfDepth * 2f)).Append(';')
                            .Append(Number(layer.RoomShell ? -halfDepth : layer.Z - layer.HalfDepth)).Append(';')
                            .Append(Number(layer.Rule.Offset ?? 0f)).Append(';').Append(layer.Rule.Side).Append(';').Append(layer.Clamped ? "yes" : "no").Append("\r\n");
                        if (layer.RoomShell) continue;
                        var row = new XElement("Object", new XAttribute("prefab", key.Prefab), new XAttribute("enabled", "false"),
                            new XAttribute("offset", Number(layer.Rule.Offset ?? 0f)), new XAttribute("scale", Number(layer.Rule.Scale ?? 1f)),
                            new XAttribute("side", layer.Rule.Side ?? "both"));
                        if (key.EditorId.HasValue)
                        { row.SetAttributeValue("submarine", key.Submarine); row.SetAttributeValue("editorId", key.EditorId.Value); }
                        else row.SetAttributeValue("entityId", key.RuntimeId);
                        if (layer.Rule.Depth.HasValue) row.SetAttributeValue("depth", Number(layer.Rule.Depth.Value));
                        root.Add(row);
                    }
                    string csvPath = Path.Combine(folder, "BaroDepth.objects.csv");
                    File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(true));
                    new XDocument(root).Save(Path.Combine(folder, "BaroDepth.object-templates.xml"));
                    Log("Exported IDs and XML templates: " + folder, Color.Cyan);
                    if (hoverItem != null)
                    {
                        LayerPlacement p = Placement(hoverItem);
                        Log("Pointed item: " + DescribeItem(hoverItem), Color.Cyan);
                        if (p.Key != null) Log(new XElement("Object", new XAttribute("entityId", p.Key.RuntimeId),
                            new XAttribute("offset", "0"), new XAttribute("scale", "1")).ToString(SaveOptions.DisableFormatting), Color.Cyan);
                    }
                    Notify("IDs exported next to BaroDepth.xml (path in F3)", Color.Cyan);
                }
                catch (Exception ex) { Log("Export failed: " + ex.Message, Color.Orange); Notify("Export failed — F3", Color.Orange); }
            }
            private string DescribeItem(Item item)
            {
                if (!settings.ShowIds) return item.Name.ToString();
                EntityKey key = Placement(item).Key ?? identity.Get(item);
                return item.Name + " | " + key.Prefab + " | entity " + key.RuntimeId +
                    (key.EditorId.HasValue ? " / editor " + key.EditorId.Value : "");
            }

            private void DrawNativeEntity(MapEntity entity)
            {
                if (entity is Item item && itemDrawWithTint != null)
                {
                    itemDrawWithTint.Invoke(item, new object[] { captureBatch, false, true, (Color?)item.SpriteColor, null });
                    itemDrawWithTint.Invoke(item, new object[] { captureBatch, false, false, (Color?)item.SpriteColor, null });
                }
                else
                {
                    entity.Draw(captureBatch, false, true);
                    entity.Draw(captureBatch, false, false);
                }
            }

            private void DrawFallback(MapEntity entity, Bounds2 b, float z, float depth = 0f)
            {
                if (!Visible(b, z, depth)) return;
                // Like the original mod's X-ray pass: draw the ENTITY, not an upright
                // copy of Sprite.SourceRect. Native drawing retains rotation, flips,
                // tiling, component sprites and PhysicsBody's render interpolation.
                int slices = 1; // warm-up only; no repeated wire/object slices
                Matrix oldWorld = cutoutEffect.World;
                try
                {
                    for (int i = 0; i < slices; i++)
                    {
                        float sliceZ = slices == 1 ? z : z + (i / (float)(slices - 1) * 2f - 1f) * depth;
                        cutoutEffect.World = Matrix.CreateScale(1f, -1f, 0f) *
                        Matrix.CreateTranslation(-eye.X, -eye.Y, sliceZ);
                        bool begun = false;
                        try
                        {
                            captureBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied,
                                SamplerState.LinearClamp, DepthStencilState.Default, RasterizerState.CullNone,
                                cutoutEffect, Matrix.Identity);
                            begun = true;
                            DrawNativeEntity(entity);
                        }
                        finally { if (begun) captureBatch.End(); }
                        frameDrawCalls++; // batch count; native sprites inside may make extra GPU calls
                    }
                }
                catch (Exception ex)
                {
                    ResetCaptureBatch();
                    WarnOnce("native-lod-" + entity.GetType().Name, "Native distant draw failed: " + ex);
                    AddWireBox(b, z, Math.Max(depth, 2f), new Color(77, 169, 193, 110));
                }
                finally { cutoutEffect.World = oldWorld; }
            }

            private void DrawLayered(MapEntity entity, Stamp stamp)
            {
                Bounds2 b = RenderBounds(entity, stamp);
                LayerPlacement layer = Placement(entity);
                for (int bank = -1; bank <= 1; bank += 2)
                {
                    if (!BankEnabled(layer, bank)) continue;
                    float z = bank * layer.Z;
                    if (!Visible(b, z, layer.HalfDepth)) continue;
                    if (stamp?.HasImage == true)
                    {
                        stamp.LastSeen = now; stamp.LastDrawn = now;
                        if (layer.HalfDepth <= 0.001f) DrawCard(stamp.Texture, b, z, 1f, Color.White);
                        else DrawVolume(stamp, b, z, layer.HalfDepth, RenderMode.Contours, false);
                    }
                    else DrawFallback(entity, b, bank * (layer.Z - layer.HalfDepth));
                }
            }

            private void AddWireBox(Bounds2 b, float z, float depth, Color color)
            {
                float l = b.Left - eye.X, r = b.Right - eye.X, t = b.Top - eye.Y, bottom = b.Bottom - eye.Y;
                Vector3[] p = wireCorners;
                p[0] = new Vector3(l, t, z - depth); p[1] = new Vector3(r, t, z - depth);
                p[2] = new Vector3(r, bottom, z - depth); p[3] = new Vector3(l, bottom, z - depth);
                p[4] = new Vector3(l, t, z + depth); p[5] = new Vector3(r, t, z + depth);
                p[6] = new Vector3(r, bottom, z + depth); p[7] = new Vector3(l, bottom, z + depth);
                foreach (int index in wireEdges) xrayLines.Add(new VertexPositionColor(p[index], color));
            }

            private void FlushWireBoxes()
            {
                if (xrayLines.Count == 0) return;
                if (lineBuffer.Length < xrayLines.Count) lineBuffer = new VertexPositionColor[xrayLines.Count * 2];
                xrayLines.CopyTo(lineBuffer);
                gd.BlendState = BlendState.NonPremultiplied;
                gd.DepthStencilState = xrayView ? DepthStencilState.None : DepthStencilState.DepthRead;
                gd.RasterizerState = RasterizerState.CullNone;
                solidEffect.World = Matrix.Identity;
                solidEffect.TextureEnabled = false;
                try
                {
                    foreach (EffectPass pass in solidEffect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        // Split the batch: Reach-profile devices have a primitive-count limit.
                        const int verticesPerBatch = 24000;
                        for (int start = 0; start < xrayLines.Count; start += verticesPerBatch)
                        {
                            int n = Math.Min(verticesPerBatch, xrayLines.Count - start);
                            gd.DrawUserPrimitives(PrimitiveType.LineList, lineBuffer, start, n / 2);
                            frameDrawCalls++;
                        }
                    }
                }
                finally { solidEffect.TextureEnabled = true; }
            }

            private void DrawStructures(RenderMode mode)
            {
                xrayLines.Clear();
                foreach (Structure structure in structures)
                {
                    if (structure == null || structure.Removed || Placement(structure).Banks == 0) continue;
                    staticStamps.TryGetValue(structure, out Stamp stamp);
                    Bounds2 b = RenderBounds(structure, stamp);
                    if (structure.IsPlatform)
                    {
                        // Hatches/walkways sit on invisible platforms. Platforms
                        // are paper-thin décor: draw as a flat card with the
                        // original texture, no extrusion, no stretch.
                        if (xrayView) { AddWireBox(b, 0f, 4f, new Color(60, 111, 141, 60)); continue; }
                        DrawCard(stamp?.Texture, b, 0f, 1f, Color.White);
                        continue;
                    }
                    if (!structure.HasBody)
                    {
                        if (xrayView)
                        {
                            LayerPlacement layer = Placement(structure);
                            for (int bank = -1; bank <= 1; bank += 2)
                            if (Visible(b, bank * layer.Z, layer.HalfDepth))
                            AddWireBox(b, bank * layer.Z, layer.HalfDepth, new Color(60, 111, 141, 60));
                        }
                        else DrawLayered(structure, stamp);
                        continue;
                    }
                    float depth = ShellDepth(structure);
                    if (!Visible(b, 0f, depth)) continue;
                    if (stamp != null) { stamp.LastSeen = now; stamp.LastDrawn = now; }
                    if (xrayView) AddWireBox(b, 0f, depth, new Color(85, 157, 189, 105));
                    else if (stamp?.HasImage == true) DrawVolume(stamp, b, 0f, depth, RenderMode.Contours, false);
                    else
                    {
                        DrawBoxSides(b, 0f, depth, new Color(67, 84, 99));
                        DrawFallback(structure, b, -depth); DrawFallback(structure, b, depth);
                    }
                }
            }

            private static bool DoorOpen(Door door) { return door.OpenState >= 0.999f; }

            private static Bounds2 DoorProxy(Item item)
            {
                Bounds2 b = EntityBounds(item);
                // Closed: the whole door face is clickable (hatches included).
                Door door = item.GetComponent<Door>();
                if (door == null || door.OpenState < 0.02f) return b;
                // Open: small hotspot at the frame so the doorway stays passable
                // for clicks meant for objects behind it.
                Vector2 center = b.Center;
                if (door.IsHorizontal)
                return new Bounds2(center.X - 7f, b.Top - 13f, center.X + 7f, b.Top - 3f);
                return new Bounds2(b.Left + 3f, center.Y - 9f, b.Left + 13f, center.Y + 9f);
            }

            private static double nextDoorLog;

            private void DrawItems(RenderMode mode)
            {
                // Door diagnostics: ALWAYS log to file + console once per 2 s
                // while FP is on. No toggle needed.
                if (now > nextDoorLog)
                {
                    nextDoorLog = now + 10.0;
                    int doorsTotal = 0, doorsDrawn = 0, doorsOpen = 0, doorsNoStamp = 0, doorsCulled = 0;
                    string sample = "";
                    foreach (Item item in items)
                    {
                        var dr = item?.GetComponent<Door>();
                        if (dr == null) continue;
                        doorsTotal++;
                        if (DoorOpen(dr)) { doorsOpen++; continue; }
                        var layer = Placement(item);
                        if (layer.Banks == 0) { doorsCulled++; continue; }
                        staticStamps.TryGetValue(item, out Stamp st2);
                        bool hasImg = st2?.HasImage == true;
                        if (!hasImg) { doorsNoStamp++; continue; }
                        doorsDrawn++;
                        if (sample.Length < 140) sample += "; " + item.Prefab?.Identifier + "#" + item.ID + " z=" + layer.Z.ToString("0") + " hd=" + layer.HalfDepth.ToString("0") + " os=" + dr.OpenState.ToString("0.00");
                    }
                    string line = "doors: total=" + doorsTotal + " drawn=" + doorsDrawn + " open=" + doorsOpen
                        + " noStamp=" + doorsNoStamp + " culled=" + doorsCulled + " sample[" + sample + "]";
                    Log(line, Color.Cyan);
                    try
                    {
                        string folder = Path.GetDirectoryName(SettingsStore.ActivePath ?? Path.Combine(SettingsStore.UserFolder, "BaroDepth.xml"));
                        if (folder != null)
                        {
                            Directory.CreateDirectory(folder);
                            File.AppendAllText(Path.Combine(folder, "BaroDepth.debug.log"),
                                DateTime.Now.ToString("HH:mm:ss") + " " + line + "\r\n");
                        }
                    }
                    catch { }
                }
                foreach (Item item in items)
                {
                    if (item == null || item.Removed || item.ParentInventory != null || Placement(item).Banks == 0) continue;
                    if (item.GetComponent<Wire>() != null) continue;
                    staticStamps.TryGetValue(item, out Stamp stamp);
                    Door door = item.GetComponent<Door>();
                    if (door == null) { DrawLayered(item, stamp); continue; }

                    Bounds2 b = EntityBounds(item);
                    if (!Visible(b, 0f, 20f)) continue;

                    fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied,
                        SamplerState.LinearClamp, null, RasterizerState.CullNone,
                        null, WallMatrix(0f));
                    door.Draw(fpBatch, false, 0f, Color.White);
                    fpBatch.End();

                    // Wireframe box in X-ray for reference
                    if (xrayView) { AddWireBox(b, 0f, 12f, new Color(82, 182, 204, 125)); }
                }
            }

            private void DrawCharacters(RenderMode mode)
            {
                foreach (Character character in characters)
                {
                    if (!characterStamps.TryGetValue(character, out Stamp stamp) || !stamp.HasImage || stamp.Failed) continue;
                    if (mode == RenderMode.Boxes) DrawCard(stamp.Texture, stamp.Bounds, 0f, 1f, Color.White);
                    else DrawSlices(stamp.Texture, stamp.Bounds, 0f,
                        MathHelper.Clamp(stamp.Bounds.Height * 0.075f, 10f, 35f), mode == RenderMode.Slices ? 11 : 5, true);
                }
            }

            private void DrawVolumeTinted(Stamp stamp, Bounds2 bounds, float z, float depth, RenderMode mode, Color tint)
            {
                if (stamp == null || !stamp.HasImage || stamp.Texture == null || stamp.Texture.IsDisposed)
                {
                    DrawBoxSides(bounds, z, depth, tint);
                    return;
                }
                DrawCard(stamp.Texture, bounds, z - depth, 1f, tint);
                DrawCard(stamp.Texture, bounds, z + depth, 1f, tint);
            }

            private void DrawVolume(Stamp stamp, Bounds2 bounds, float z, float depth, RenderMode mode, bool taper)
            {
                if (stamp == null || !stamp.HasImage || stamp.Texture == null || stamp.Texture.IsDisposed)
                {
                    DrawBoxSides(bounds, z, depth, new Color(87, 107, 119));
                    return;
                }
                if (mode == RenderMode.Slices)
                {
                    // Structural cross-sections stay full size to avoid opening gaps in floors.
                    DrawSlices(stamp.Texture, bounds, z, depth, taper ? 11 : 9, taper);
                    if (!taper && stamp.Sides != null) DrawSides(stamp.Sides, bounds, z, depth);
                    return;
                }
                DrawCard(stamp.Texture, bounds, z - depth, 1f, new Color(197, 209, 224));
                if (mode == RenderMode.Contours)
                {
                    // No rectangle fallback for an empty/failed alpha mask: it would
                    // put an invisible wall across an open door or transparent sprite.
                    if (stamp.Sides != null) DrawSides(stamp.Sides, bounds, z, depth);
                }
                else DrawBoxSides(bounds, z, depth, new Color(91, 112, 126));
                DrawCard(stamp.Texture, bounds, z + depth, 1f, Color.White);
            }

            private void DrawSlices(Texture2D texture, Bounds2 bounds, float z, float depth, int count, bool taper)
            {
                for (int i = 0; i < count; i++)
                {
                    float t = (float)i / (count - 1) * 2f - 1f;
                    float scale = taper ? 0.64f + 0.36f * (float)Math.Sqrt(Math.Max(0f, 1f - t * t)) : 1f;
                    int shade = (int)(205f + 50f * (1f - Math.Abs(t)));
                    DrawCard(texture, bounds, z + t * depth, scale, new Color(shade, shade, shade));
                }
            }

            private void DrawCard(Texture2D texture, Bounds2 bounds, float z, float scale, Color tint)
            {
                Vector2 c = bounds.Center - eye;
                float hw = bounds.Width * 0.5f * scale, hh = bounds.Height * 0.5f * scale;
                DrawQuad(texture, new Vector3(c.X - hw, c.Y + hh, z), new Vector3(c.X + hw, c.Y + hh, z),
                    new Vector3(c.X - hw, c.Y - hh, z), new Vector3(c.X + hw, c.Y - hh, z),
                    0, 0, 1, 1, tint, true, Matrix.Identity, false);
            }

            private void DrawBoxSides(Bounds2 bounds, float z, float depth, Color tint)
            {
                float l = bounds.Left - eye.X, r = bounds.Right - eye.X;
                float t = bounds.Top - eye.Y, b = bounds.Bottom - eye.Y;
                float back = z - depth, front = z + depth;
                DrawQuad(metal, new Vector3(l, t, back), new Vector3(r, t, back), new Vector3(l, t, front), new Vector3(r, t, front),
                    0, 0, bounds.Width / 64f, depth / 32f, tint, false, Matrix.Identity, true);
                DrawQuad(metal, new Vector3(l, b, front), new Vector3(r, b, front), new Vector3(l, b, back), new Vector3(r, b, back),
                    0, 0, bounds.Width / 64f, depth / 32f, Shade(tint, 0.66f), false, Matrix.Identity, true);
                DrawQuad(metal, new Vector3(l, t, back), new Vector3(l, t, front), new Vector3(l, b, back), new Vector3(l, b, front),
                    0, 0, depth / 32f, bounds.Height / 64f, Shade(tint, 0.82f), false, Matrix.Identity, true);
                DrawQuad(metal, new Vector3(r, t, front), new Vector3(r, t, back), new Vector3(r, b, front), new Vector3(r, b, back),
                    0, 0, depth / 32f, bounds.Height / 64f, Shade(tint, 0.90f), false, Matrix.Identity, true);
            }

            private void DrawSides(Mesh mesh, Bounds2 bounds, float z, float depth)
            {
                if (mesh.Vertices == null || mesh.Vertices.IsDisposed) return;
                Vector2 center = bounds.Center - eye;
                solidEffect.World = Matrix.CreateScale(bounds.Width, bounds.Height, depth) *
                Matrix.CreateTranslation(center.X, center.Y, z);
                solidEffect.Texture = white;
                gd.BlendState = BlendState.Opaque; gd.DepthStencilState = DepthStencilState.Default;
                gd.RasterizerState = sideRasterizer; gd.SamplerStates[0] = SamplerState.PointClamp;
                gd.SetVertexBuffer(mesh.Vertices); gd.Indices = mesh.Indices;
                foreach (EffectPass pass in solidEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, mesh.VertexCount, 0, mesh.TriangleCount);
                    frameDrawCalls++;
                }
            }

            /// <summary>
            /// Decorative front-facing quad: depth-tested against the scene but
            /// never writes depth, so it cannot occlude items or flicker by
            /// fighting its own depth across frames.
            /// </summary>
            private void DrawQuadFrontNoWrite(Texture2D texture, Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br,
                float u0, float v0, float u1, float v1, Color tint)
            {
                quad[0] = new VertexPositionColorTexture(tl, tint, new Vector2(u0, v0));
                quad[1] = new VertexPositionColorTexture(tr, tint, new Vector2(u1, v0));
                quad[2] = new VertexPositionColorTexture(bl, tint, new Vector2(u0, v1));
                quad[3] = new VertexPositionColorTexture(br, tint, new Vector2(u1, v1));
                gd.BlendState = BlendState.Opaque;
                gd.DepthStencilState = DepthStencilState.DepthRead;
                gd.RasterizerState = RasterizerState.CullNone;
                gd.SamplerStates[0] = SamplerState.LinearWrap;
                solidEffect.World = Matrix.Identity; solidEffect.Texture = texture;
                foreach (EffectPass pass in solidEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quad, 0, 4, quadIndices, 0, 2);
                    frameDrawCalls++;
                }
            }

            private void DrawQuad(Texture2D texture, Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br,
                float u0, float v0, float u1, float v1, Color tint, bool cutout, Matrix world, bool wrap,
                bool translucent = false)
            {
                quad[0] = new VertexPositionColorTexture(tl, tint, new Vector2(u0, v0));
                quad[1] = new VertexPositionColorTexture(tr, tint, new Vector2(u1, v0));
                quad[2] = new VertexPositionColorTexture(bl, tint, new Vector2(u0, v1));
                quad[3] = new VertexPositionColorTexture(br, tint, new Vector2(u1, v1));
                gd.BlendState = translucent ? BlendState.NonPremultiplied : BlendState.Opaque;
                gd.DepthStencilState = translucent ? DepthStencilState.DepthRead : DepthStencilState.Default;
                gd.RasterizerState = cutout ? capRasterizer : RasterizerState.CullNone;
                gd.SamplerStates[0] = wrap ? SamplerState.LinearWrap : SamplerState.LinearClamp;
                Effect effect;
                if (cutout)
                { cutoutEffect.World = world; cutoutEffect.Texture = texture; effect = cutoutEffect; }
                else
                { solidEffect.World = world; solidEffect.Texture = texture; effect = solidEffect; }
                foreach (EffectPass pass in effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quad, 0, 4, quadIndices, 0, 2);
                    frameDrawCalls++;
                }
            }

            private void DrawWater()
            {
                foreach (Hull hull in hulls)
                {
                    float waterDepth = CorridorDepth(hull.Submarine);
                    if (hull.Removed || hull.WaterVolume <= 0f) continue;
                    Rectangle r = hull.WorldRect;
                    if (r.Width <= 0 || r.Height <= 0) continue;
                    float fraction = MathHelper.Clamp(hull.WaterVolume / ((float)r.Width * r.Height), 0f, 1f);
                    Vector2 d = SubDrawCorrection(hull.Submarine) - eye;
                    float y = r.Y - r.Height + r.Height * fraction + d.Y;
                    // Keep the waterline off exact wall lines: coplanar quads
                    // flicker (z-fighting) at the floor/ceiling seam.
                    y = MathHelper.Clamp(y, r.Bottom - r.Height * 0.999f + d.Y, r.Bottom - 1f + d.Y);
                    y += 0.5f;
                    DrawQuad(white, new Vector3(r.X + d.X, y, -waterDepth), new Vector3(r.Right + d.X, y, -waterDepth),
                        new Vector3(r.X + d.X, y, waterDepth), new Vector3(r.Right + d.X, y, waterDepth),
                        0, 0, 1, 1, new Color(37, 128, 165, 105), false, Matrix.Identity, false, true);
                }
            }

            private Mesh BuildOutline(Stamp stamp)
            {
                Texture2D texture = stamp.Texture;
                int width = texture.Width, height = texture.Height;
                Color[] pixels = new Color[width * height];
                texture.GetData(pixels); // local uncompressed RT, not a compressed game texture
                int step = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (float)OutlineGridMaxSide));
                int gw = (width + step - 1) / step, gh = (height + step - 1) / step;
                bool[] mask = new bool[gw * gh];
                Color[] colors = new Color[mask.Length];
                for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    int count = 0, solid = 0, red = 0, green = 0, blue = 0;
                    for (int py = y * step; py < Math.Min(height, (y + 1) * step); py++)
                    for (int px = x * step; px < Math.Min(width, (x + 1) * step); px++)
                    {
                        Color c = pixels[py * width + px]; count++;
                        if (c.A < AlphaCutoff) continue;
                        solid++;
                        red += Math.Min(255, c.R * 255 / c.A);
                        green += Math.Min(255, c.G * 255 / c.A);
                        blue += Math.Min(255, c.B * 255 / c.A);
                    }
                    int index = y * gw + x;
                    mask[index] = solid > 0; // any coverage joins the contour: closes corner gaps on round sprites
                    colors[index] = solid == 0 ? Color.White : new Color(red / solid, green / solid, blue / solid, 255);
                }
                float anchorDistance = float.MaxValue;
                for (int my = 0; my < gh; my++)
                for (int mx = 0; mx < gw; mx++)
                {
                    if (!mask[my * gw + mx]) continue;
                    float ax = Math.Min(width - 0.5f, (mx + 0.5f) * step) / width;
                    float ay = Math.Min(height - 0.5f, (my + 0.5f) * step) / height;
                    float ad = (ax - 0.5f) * (ax - 0.5f) + (ay - 0.5f) * (ay - 0.5f);
                    if (ad < anchorDistance) { anchorDistance = ad; stamp.AnchorUV = new Vector2(ax, ay); }
                }
                stamp.Mask = mask; stamp.MaskWidth = gw; stamp.MaskHeight = gh;
                stamp.MaskStep = step; stamp.PixelWidth = width; stamp.PixelHeight = height;
                bool Opaque(int x, int y) => x >= 0 && y >= 0 && x < gw && y < gh && mask[y * gw + x];
                var vertices = new List<VertexPositionColorTexture>(512);
                var indices = new List<short>(768);
                bool Edge(int x, int y, int side)
                {
                    if (!Opaque(x, y)) return false;
                    switch (side)
                    {
                        case 0: return !Opaque(x - 1, y);
                        case 1: return !Opaque(x + 1, y);
                        case 2: return !Opaque(x, y - 1);
                        default: return !Opaque(x, y + 1);
                    }
                }
                // Greedy runs merge collinear exposed edges. A solid N*N image becomes
                // four side quads instead of 4*N quads (and not N*N cubes).
                for (int side = 0; side < 4; side++)
                {
                    bool vertical = side < 2;
                    int outer = vertical ? gw : gh, inner = vertical ? gh : gw;
                    for (int a = 0; a < outer; a++)
                    {
                        int b = 0;
                        while (b < inner)
                        {
                            int x = vertical ? a : b, y = vertical ? b : a;
                            if (!Edge(x, y, side)) { b++; continue; }
                            int start = b;
                            int rr = 0, gg = 0, bb = 0, n = 0;
                            while (b < inner)
                            {
                                x = vertical ? a : b; y = vertical ? b : a;
                                if (!Edge(x, y, side)) break;
                                Color color = colors[y * gw + x]; rr += color.R; gg += color.G; bb += color.B; n++; b++;
                            }
                            Color tint = Shade(new Color(rr / n, gg / n, bb / n, 255),
                                side == 2 ? 1f : side == 3 ? 0.62f : side == 0 ? 0.78f : 0.90f);
                            Vector3 tl, tr, bl, br;
                            if (vertical)
                            {
                                float xx = Math.Min(width, (a + (side == 1 ? 1 : 0)) * step) / (float)width - 0.5f;
                                float top = 0.5f - Math.Min(height, start * step) / (float)height;
                                float bottom = 0.5f - Math.Min(height, b * step) / (float)height;
                                tl = new Vector3(xx, top, -1); tr = new Vector3(xx, top, 1);
                                bl = new Vector3(xx, bottom, -1); br = new Vector3(xx, bottom, 1);
                            }
                            else
                            {
                                float yy = 0.5f - Math.Min(height, (a + (side == 3 ? 1 : 0)) * step) / (float)height;
                                float left = Math.Min(width, start * step) / (float)width - 0.5f;
                                float right = Math.Min(width, b * step) / (float)width - 0.5f;
                                tl = new Vector3(left, yy, -1); tr = new Vector3(right, yy, -1);
                                bl = new Vector3(left, yy, 1); br = new Vector3(right, yy, 1);
                            }
                            if (vertices.Count / 4 >= MaxSideQuads)
                            {
                                WarnOnce("outline-budget", "An outline exceeded the geometry budget; cutout planes are used.");
                                return null;
                            }
                            AddMeshQuad(vertices, indices, tl, tr, bl, br, tint);
                        }
                    }
                }
                return Mesh.Create(gd, vertices, indices);
            }

            private static void AddMeshQuad(List<VertexPositionColorTexture> vertices, List<short> indices,
                Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, Color tint)
            {
                if (vertices.Count + 4 > short.MaxValue) throw new InvalidOperationException("Outline index budget exceeded.");
                short offset = (short)vertices.Count;
                vertices.Add(new VertexPositionColorTexture(tl, tint, Vector2.Zero));
                vertices.Add(new VertexPositionColorTexture(tr, tint, Vector2.UnitX));
                vertices.Add(new VertexPositionColorTexture(bl, tint, Vector2.UnitY));
                vertices.Add(new VertexPositionColorTexture(br, tint, Vector2.One));
                indices.Add(offset); indices.Add((short)(offset + 1)); indices.Add((short)(offset + 2));
                indices.Add((short)(offset + 2)); indices.Add((short)(offset + 1)); indices.Add((short)(offset + 3));
            }

            private static Color Shade(Color color, float value)
            {
                return new Color((int)(color.R * value), (int)(color.G * value), (int)(color.B * value), color.A);
            }

            private Vector3 ScreenRay(Point pixel, float yaw, float pitch)
            {
                Vector3 forward = Forward(yaw, pitch);
                Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.Up));
                Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
                float h = Math.Max(1, GameMain.GraphicsHeight), w = Math.Max(1, GameMain.GraphicsWidth);
                float scale = (float)Math.Tan(MathHelper.ToRadians(frameFov > 0f ? frameFov : FieldOfViewDegrees) * 0.5f);
                float x = (2f * pixel.X / w - 1f) * scale * w / h;
                float y = (1f - 2f * pixel.Y / h) * scale;
                return Vector3.Normalize(forward + right * x + up * y);
            }

            private bool Project(Vector3 relative, out Vector2 screen)
            {
                screen = Vector2.Zero;
                Vector4 clip = Vector4.Transform(new Vector4(relative, 1f), view * projection);
                if (clip.W <= 0.001f || clip.Z < 0f || clip.Z > clip.W) return false;
                float x = clip.X / clip.W, y = clip.Y / clip.W;
                if (Math.Abs(x) > 1.05f || Math.Abs(y) > 1.05f) return false;
                screen = new Vector2((x + 1f) * 0.5f * GameMain.GraphicsWidth,
                    (1f - y) * 0.5f * GameMain.GraphicsHeight);
                return true;
            }

            private static bool Slab(float direction, float min, float max, ref float enter, ref float exit)
            {
                if (Math.Abs(direction) < 0.0000001f) return min <= 0f && max >= 0f;
                float a = min / direction, b = max / direction;
                if (a > b) { float swap = a; a = b; b = swap; }
                enter = Math.Max(enter, a); exit = Math.Min(exit, b);
                return enter <= exit;
            }

            private bool RayPrism(Vector3 ray, Bounds2 b, float z, float depth, Stamp stamp, float limit, out float hit)
            {
                hit = 0f;
                float enter = 0.02f, exit = limit;
                if (!Slab(ray.X, b.Left - eye.X, b.Right - eye.X, ref enter, ref exit) ||
                    !Slab(ray.Y, b.Bottom - eye.Y, b.Top - eye.Y, ref enter, ref exit) ||
                    !Slab(ray.Z, z - depth, z + depth, ref enter, ref exit)) return false;
                if (stamp?.Mask == null || stamp.MaskWidth <= 0 || stamp.MaskHeight <= 0)
                { hit = enter; return true; } // conservative LOD until its alpha cache is ready
                // Traverse the same XY alpha grid used to extrude the side mesh. A hole in
                // a ladder is a hole for picking as well. Last grid cells may be smaller.
                float cellsX = stamp.PixelWidth / (float)stamp.MaskStep;
                float cellsY = stamp.PixelHeight / (float)stamp.MaskStep;
                float ox = (eye.X - b.Left) / b.Width * cellsX;
                float oy = (b.Top - eye.Y) / b.Height * cellsY;
                float dx = ray.X / b.Width * cellsX, dy = -ray.Y / b.Height * cellsY;
                float epsilon = Math.Min(0.001f, Math.Max(0f, (exit - enter) * 0.25f));
                int ix = Math.Max(0, Math.Min(stamp.MaskWidth - 1, (int)Math.Floor(ox + dx * (enter + epsilon))));
                int iy = Math.Max(0, Math.Min(stamp.MaskHeight - 1, (int)Math.Floor(oy + dy * (enter + epsilon))));
                int sx = dx > 0f ? 1 : -1, sy = dy > 0f ? 1 : -1;
                float t = enter;
                int maxSteps = stamp.MaskWidth + stamp.MaskHeight + 4;
                for (int n = 0; n < maxSteps; n++)
                {
                    if (ix < 0 || iy < 0 || ix >= stamp.MaskWidth || iy >= stamp.MaskHeight || t > exit) return false;
                    if (stamp.Mask[iy * stamp.MaskWidth + ix]) { hit = t; return true; }
                    float tx = Math.Abs(dx) < 0.0000001f ? float.PositiveInfinity : ((dx > 0 ? ix + 1 : ix) - ox) / dx;
                    float ty = Math.Abs(dy) < 0.0000001f ? float.PositiveInfinity : ((dy > 0 ? iy + 1 : iy) - oy) / dy;
                    float next = Math.Min(tx, ty);
                    if (float.IsInfinity(next) || next > exit + 0.0001f) return false;
                    if (tx <= next + 0.00001f) ix += sx;
                    if (ty <= next + 0.00001f) iy += sy;
                    t = Math.Max(t, next);
                }
                return false;
            }

            private bool RayLayered(Vector3 ray, MapEntity entity, Stamp stamp, float limit, out float hit)
            {
                hit = limit;
                if (entity is Item item && item.GetComponent<Wire>() != null)
                    return wireLookup.TryGetValue(item, out WireVisual wire) && RayWire(ray, wire, limit, out hit);
                LayerPlacement layer = Placement(entity);
                Bounds2 b = RenderBounds(entity, stamp);
                bool found = false;
                for (int bank = -1; bank <= 1; bank += 2)
                    if (BankEnabled(layer, bank) && RayPrism(ray, b, bank * layer.Z, Math.Max(0.001f, layer.HalfDepth), stamp, hit, out float t))
                    { hit = t; found = true; }
                return found;
            }

            private float OcclusionLimit(Vector3 ray, float limit, Item exclude, bool includeItems)
            {
                if (!xrayView)
                {
                    foreach (Structure structure in structures)
                    {
                        if (structure.Removed || Placement(structure).Banks == 0) continue;
                        staticStamps.TryGetValue(structure, out Stamp stamp);
                        float t;
                        bool hit = structure.HasBody
                        ? RayPrism(ray, RenderBounds(structure, stamp), 0f, ShellDepth(structure), stamp, limit, out t)
                        : RayLayered(ray, structure, stamp, limit, out t);
                        if (hit) limit = Math.Min(limit, t);
                    }
                }
                if (includeItems)
                {
                    foreach (Item item in items)
                    {
                        if (item.Removed || item.ParentInventory != null || ReferenceEquals(item, exclude) || Placement(item).Banks == 0) continue;
                        Door door = item.GetComponent<Door>();
                        if (door != null && (DoorOpen(door) || xrayView)) continue;
                        staticStamps.TryGetValue(item, out Stamp stamp);
                        float t;
                        bool hit = door == null
                        ? RayLayered(ray, item, stamp, limit, out t)
                        : RayPrism(ray, RenderBounds(item, stamp), 0f, CorridorDepth(item.Submarine) - 2f, stamp, limit, out t);
                        if (hit) limit = Math.Min(limit, t);
                    }
                }
                return limit;
            }

            public bool TryPick(Point pixel, float yaw, float pitch, bool allowLabels, out Item target, out Vector2 targetWorld)
            {
                target = null; targetWorld = Vector2.Zero;
                if (currentPlayer == null || disposed || scene == null) return false;
                if (allowLabels)
                {
                    foreach (Hint hint in hintBoxes)
                    if (!hint.Item.Removed && hint.Item.ParentInventory == null && hint.Box.Contains(pixel.X, pixel.Y))
                    { target = hint.Item; targetWorld = target.WorldPosition; return true; }
                }
                Vector3 ray = ScreenRay(pixel, yaw, pitch);
                float closest = OcclusionLimit(ray, farPlane, null, false);
                foreach (Item item in items)
                {
                    if (item.Removed || item.ParentInventory != null || Placement(item).Banks == 0) continue;
                    Door door = item.GetComponent<Door>();
                    staticStamps.TryGetValue(item, out Stamp stamp);
                    float t;
                    bool hit;
                    if (door == null) hit = RayLayered(ray, item, stamp, closest, out t);
                    else if (DoorOpen(door) || xrayView)
                    hit = RayPrism(ray, DoorProxy(item), 0f, 12f, null, closest, out t);
                    else hit = RayPrism(ray, RenderBounds(item, stamp), 0f, CorridorDepth(item.Submarine) - 2f, stamp, closest, out t);
                    if (hit && t < closest) { closest = t; target = item; }
                }
                if (target == null) return false;
                Vector2 drawPoint = eye + new Vector2(ray.X, ray.Y) * closest;
                targetWorld = drawPoint - SubDrawCorrection(target.Submarine);
                return true;
            }

            private Vector3 ItemAnchor(Item item, int bank = 1)
            {
                if (wireLookup.TryGetValue(item, out WireVisual wire) && wire.Sections.Count > 0)
                {
                    Wire.WireSection segment = wire.Sections[wire.Sections.Count / 2];
                    Vector2 center = (segment.Start + segment.End) * 0.5f + WireOffset(wire) - eye;
                    LayerPlacement p = Placement(item);
                    return new Vector3(center, bank * (p.Z - p.HalfDepth));
                }
                Door door = item.GetComponent<Door>();
                if (door != null && (DoorOpen(door) || xrayView))
                {
                    Vector2 center = DoorProxy(item).Center - eye;
                    return new Vector3(center.X, center.Y, 0f);
                }
                staticStamps.TryGetValue(item, out Stamp stamp);
                Bounds2 b = RenderBounds(item, stamp);
                Vector2 uv = stamp?.Mask != null ? stamp.AnchorUV : new Vector2(0.5f, 0.5f);
                LayerPlacement layer = Placement(item);
                float z = door == null ? bank * (layer.Z - layer.HalfDepth)
                : bank * (CorridorDepth(item.Submarine) - 2f);
                return new Vector3(b.Left + b.Width * uv.X - eye.X, b.Top - b.Height * uv.Y - eye.Y, z);
            }





            private void PrepareHints()
            {
                hintBoxes.Clear(); hintCandidates.Clear();
                Vector2 screenCenter = new Vector2(GameMain.GraphicsWidth / 2f, GameMain.GraphicsHeight / 2f);
                foreach (Item item in items)
                {
                    if (item.Removed || item.ParentInventory != null) continue;
                    // Far items cannot be clicked meaningfully; skipping their
                    // occlusion rays removes the heaviest part of the label pass.
                    if (!xrayView && Vector2.DistanceSquared(item.WorldPosition, eye) > 8000f * 8000f) continue;
                    Door door = item.GetComponent<Door>();
                    int banks = door != null && (DoorOpen(door) || xrayView) ? 1 : 2;
                    for (int copy = 0; copy < banks; copy++)
                    {
                        int bank = copy == 0 ? -1 : 1;
                        if (!BankEnabled(Placement(item), bank)) continue;
                        Vector3 point = ItemAnchor(item, bank);
                        if (!Project(point, out Vector2 projected)) continue;
                        string text = item.Name.ToString();
                        if (text.Length > 31) text = text.Substring(0, 30) + "…";
                        if (settings.ShowIds)
                        {
                            EntityKey key = Placement(item).Key;
                            if (key != null) text += " #" + key.RuntimeId + (key.EditorId.HasValue ? "/" + key.EditorId.Value : "");
                        }
                        hintCandidates.Add(new Hint { Item = item, Bank = bank, Anchor = projected, Text = text,
                                Score = Vector2.DistanceSquared(projected, screenCenter) + point.LengthSquared() * 0.015f });
                    }
                }
                hintCandidates.Sort((a, b) => a.Score.CompareTo(b.Score));
                int candidates = Math.Min(36, hintCandidates.Count);
                for (int i = 0; i < candidates && hintBoxes.Count < 12; i++)
                {
                    Hint hint = hintCandidates[i];
                    Vector3 point = ItemAnchor(hint.Item, hint.Bank);
                    float distance = point.Length();
                    if (!xrayView && distance > 1f &&
                        OcclusionLimit(point / distance, distance + 0.5f, hint.Item, true) < distance - 1f) continue;
                    int w = Math.Min(360, Math.Max(100, hint.Text.Length * 11 + 12));
                    int h = 25;
                    bool placed = false;
                    for (int attempt = 0; attempt < 18; attempt++)
                    {
                        int side = attempt % 2 == 0 ? 1 : -1;
                        int row = attempt / 2;
                        int x = (int)hint.Anchor.X + (side > 0 ? 18 : -w - 18);
                        int y = (int)hint.Anchor.Y - 34 - row * 28;
                        if (y < 95) y = (int)hint.Anchor.Y + 18 + row * 28;
                        x = Math.Max(12, Math.Min(GameMain.GraphicsWidth - w - 12, x));
                        y = Math.Max(95, Math.Min(GameMain.GraphicsHeight - h - 50, y));
                        Rectangle box = new Rectangle(x, y, w, h);
                        bool overlap = false;
                        foreach (Hint existing in hintBoxes)
                        {
                            Rectangle padded = existing.Box; padded.Inflate(5, 4);
                            if (padded.Intersects(box)) { overlap = true; break; }
                        }
                        if (overlap) continue;
                        hint.Box = box; placed = true; break;
                    }
                    if (placed) hintBoxes.Add(hint);
                }
            }

            private void WarnOnce(string id, string message)
            {
                if (warnings.Add(id)) Log(message, Color.Orange);
            }

            private void DrawScreenBatch(Action action, BlendState blend)
            {
                bool begun = false;
                try
                {
                    screenBatch.Begin(SpriteSortMode.Deferred, blend, SamplerState.LinearClamp,
                        DepthStencilState.None, RasterizerState.CullNone);
                    begun = true; action();
                }
                finally { if (begun) screenBatch.End(); }
            }

            public void DrawOverlay(RenderMode mode, bool captured, bool inWater, bool hints)
            {
                using (var state = new GraphicsScope(gd))
                {
                    if (hints && GameMain.WindowActive && !UiOwnsInput())
                    {
                        // Rebuilding labels ray-tests every structure and item;
                        // once per 0.2 s is visually identical and removes the
                        // ALT stutter. Occlusion results stay cached in boxes.
                        if (now >= nextHintBuild) { PrepareHints(); nextHintBuild = now + 0.2; }
                    }
                    else { hintBoxes.Clear(); nextHintBuild = 0; }
                    DrawScreenBatch(() =>
                        {
                            int width = GameMain.GraphicsWidth, height = GameMain.GraphicsHeight;
                            if (inWater && !xrayView)
                            screenBatch.Draw(white, new Rectangle(0, 0, width, height), new Color(14, 77, 110, 24));
                            if (captured)
                            {
                                int x = width / 2, y = height / 2;
                                Color cross = hoverItem == null ? Color.White : Color.Cyan;
                                screenBatch.Draw(white, new Rectangle(x - 8, y, 5, 1), cross);
                                screenBatch.Draw(white, new Rectangle(x + 4, y, 5, 1), cross);
                                screenBatch.Draw(white, new Rectangle(x, y - 8, 1, 5), cross);
                                screenBatch.Draw(white, new Rectangle(x, y + 4, 1, 5), cross);
                                if (hoverItem != null && !hoverItem.Removed)
                                GUI.DrawString(screenBatch, new Vector2(x + 15, y + 12), DescribeItem(hoverItem), Color.Cyan);
                            }
                            foreach (Hint hint in hintBoxes)
                            {
                                Color color = ReferenceEquals(hint.Item, hoverItem) ? Color.Cyan : new Color(166, 223, 231);
                                Vector2 start = new Vector2(
                                MathHelper.Clamp(hint.Anchor.X, hint.Box.Left + 4, hint.Box.Right - 4),
                                hint.Anchor.Y > hint.Box.Bottom ? hint.Box.Bottom : hint.Box.Top);
                                ScreenLine(start, hint.Anchor, color, 1.5f);
                                Vector2 dir = start - hint.Anchor;
                                if (dir.LengthSquared() > 1f)
                                {
                                    dir.Normalize(); Vector2 side = new Vector2(-dir.Y, dir.X);
                                    ScreenLine(hint.Anchor, hint.Anchor + dir * 8f + side * 4f, color, 1.5f);
                                    ScreenLine(hint.Anchor, hint.Anchor + dir * 8f - side * 4f, color, 1.5f);
                                }
                                screenBatch.Draw(white, hint.Box, new Color(6, 18, 27, 222));
                                GUI.DrawString(screenBatch, new Vector2(hint.Box.X + 6, hint.Box.Y + 3), hint.Text, color);
                            }
                            screenBatch.Draw(white, new Rectangle(12, 12, Math.Max(100, Math.Min(width - 24, 850)), 74),
                            new Color(7, 15, 24, 210));
                            GUI.DrawString(screenBatch, new Vector2(22, 18),
                            "BARODEPTH 0.3 | CONTOURS | " + (xrayView ? "X-RAY / SEE THROUGH" : fullView ? "FULL / ALL ROOMS" : "NORMAL / NEAR") + " | depth x" + Number(settings.DepthScale) + " | width " + Number(halfDepth * 2f), Color.Cyan);
                            GUI.DrawString(screenBatch, new Vector2(22, 39),
                            "F5 off | F6 view | F7 reload XML | F8 export IDs | ALT labels | LMB release: interact", Color.White);
                            GUI.DrawString(screenBatch, new Vector2(22, 60),
                            "CPU " + lastFrameMs.ToString("F1") + " ms | passes " + frameDrawCalls + " | " +
                            items.Count + " items / " + structures.Count + " structures | cache " +
                            (cacheBytes / (1024f * 1024f)).ToString("F1") + " MiB | contours " + coveredCount + "/" + coverageTotal + " | pending " + cachePending + (budgetBlocked > 0 ? " | MEMORY LIMIT" : ""),
                            new Color(156, 184, 201));
                            if (!string.IsNullOrEmpty(notice) && now < noticeUntil)
                            GUI.DrawString(screenBatch, new Vector2(22, Math.Max(100, height - 45)), notice, noticeColor);
                        }, BlendState.NonPremultiplied);
                }
            }

            private void ScreenLine(Vector2 a, Vector2 b, Color color, float thickness)
            {
                Vector2 d = b - a;
                float length = d.Length();
                if (length < 0.1f) return;
                screenBatch.Draw(white, a, null, color, (float)Math.Atan2(d.Y, d.X),
                    new Vector2(0f, 0.5f), new Vector2(length, thickness), SpriteEffects.None, 0f);
            }

            public void ClearScene()
            {
                foreach (Stamp stamp in staticStamps.Values) stamp.Dispose();
                foreach (Stamp stamp in characterStamps.Values) stamp.Dispose();
                staticStamps.Clear(); characterStamps.Clear();
                structures.Clear(); items.Clear(); characters.Clear(); hulls.Clear();
                layers.Clear(); identity.Clear(); wireLookup.Clear(); wireGroups.Clear(); detailCandidates.Clear(); backWallDepths.Clear(); shellDepths.Clear(); backdrop.Clear(); hintBoxes.Clear(); hintCandidates.Clear();
                xrayLines.Clear(); captureSet.Clear(); hoverItem = null; currentPlayer = null; frustumReady = false;
                nextRefresh = 0; mapCount = -1; previousSub = null; cacheBytes = 0;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true; ClearScene();
                scene?.Dispose(); scene = null;
                metal?.Dispose(); white?.Dispose();
                captureBatch?.Dispose(); screenBatch?.Dispose();
                captureBlend?.Dispose(); solidEffect?.Dispose(); cutoutEffect?.Dispose();
                capRasterizer.Dispose(); sideRasterizer.Dispose();
            }
        }
    }
}
