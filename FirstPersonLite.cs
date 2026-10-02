// ============================================================================
//  FirstPersonLite.cs — чистый FP-мод на базе v1, доделанный до игры
//
//  ЧТО ЭТО:
//  Один самодостаточный CSHUB-файл. Никаких noclip'ов: движение — инъекция
//  Keys.Held (персонаж РЕАЛЬНО ходит/плывёт/лазает, сервер видит честный инпут).
//  Рендер: hull-комната с полом/потолком/стенами (текстуры структур через
//  тайлинг), z-слои через BuildWallMatrix, персонажи-биллборды с Z-offset,
//  прицел, интеракт через CursorPosition (сервер сам таргетит).
//
//  КЛАВИШИ:
//    F5  — вкл/выкл FP
//    Мышь — обзор (yaw/pitch), WASD — движение (в воде = куда смотришь),
//          у лестницы: W+взгляд вверх = лезь вверх, S+вниз = вниз
//    E / ЛКМ — использовать то, на что смотришь
//    NumPad8 — толщина лимбов персонажей (0 = плоские биллборды)
//    NumPad2 — дальность отсечения стен (клампы комнаты строже/шире)
//
//  ЧТО ВЫКИНУТО относительно v2 (сознательно):
//    экструзия спрайтов в меши, angled/special-shell проходы,debug-окна,
//    stair/damage/вода-проходы, 300 хоткеев. Каркас расширяемый.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using FarseerPhysics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FirstPersonMod
{
    public class FirstPersonLite : ACsMod
    {
        // ===== НАСТРОЙКИ =====
        private const float MouseSensitivity = 0.003f;
        private const float FovBase = MathF.PI / 2.5f;

        /// <summary>Полутолщина мира по Z (стены стоят на ±ZHalf).</summary>
        private const float ZHalf = 150f;

        private const float InteractMaxDist = 2500f;

        // ===== СОСТОЯНИЕ =====
        private static bool fpOn;
        private static float camYaw, camPitch, fov = FovBase;
        private static Vector2 savedCamPos;
        private static float savedCamZoom;
        private static bool savedLos, savedLighting;

        private static Direction lockedDir = Direction.Right;
        private static int flipCooldown;

        /// <summary>Толщина лимбов персонажей (0..5), NumPad8.</summary>
        private static float limbThickness;

        /// <summary>Множитель отсечения комнаты, NumPad2 (0.6..1.5).</summary>
        private static float clipScale = 1f;

        // ===== КОМНАТА (bounds из Hull) =====
        private static float roomLeft, roomRight, roomFloor, roomCeil;
        private static Hull currentHull;

        // ===== КЭШ СТРУКТУР (пересобирается раз в 0.5с, не каждый кадр) =====
        private static double nextRecache;
        private static readonly List<Structure> verticalWalls = new();
        private static readonly List<Structure> horizontalWalls = new();
        private static readonly List<Item> doors = new();

        // ===== РЕСУРСЫ =====
        private static Texture2D pixelTex;
        private static SpriteBatch fpBatch;
        private static FieldInfo transformField, shaderTransformField;
        private static BasicEffect basicEffect;
        private static readonly Dictionary<Texture2D, Color[]> texPixelCache = new();

        // =====================================================================
        // ЖИЗНЕННЫЙ ЦИКЛ
        // =====================================================================
        public FirstPersonLite()
        {
            GameMain.LuaCs.Hook.Add("think", "fplite_update", _ =>
            {
                try { OnUpdate(); } catch (Exception e) { LogErr(e); }
                return null;
            });

            HookPrivate("UpdateTransform", LuaCsHook.HookMethodType.Before, inst =>
            {
                if (fpOn) OverrideTransform((Camera)inst);
            });

            HookPublic("Draw", new[] { typeof(double), typeof(GraphicsDevice), typeof(SpriteBatch) },
                LuaCsHook.HookMethodType.Before, _ => { if (fpOn) PreDraw(); });

            var drawMap = typeof(GameScreen).GetMethod("DrawMap", BindingFlags.Public | BindingFlags.Instance);
            if (drawMap != null)
            {
                GameMain.LuaCs.Hook.HookMethod("fplite_drawmap", drawMap, (_, _) =>
                {
                    if (fpOn && Character.Controlled != null)
                    {
                        try { Draw3DWorld(); } catch (Exception e) { LogErr(e); }
                    }
                    return null;
                }, LuaCsHook.HookMethodType.After);
            }

            HookPublic("Draw", new[] { typeof(double), typeof(GraphicsDevice), typeof(SpriteBatch) },
                LuaCsHook.HookMethodType.After, _ => { if (fpOn) DrawOverlay(); });

            Log("FirstPersonLite loaded | F5=FP | WASD=move | E=use | NumPad8=limb thickness");
        }

        public override void Stop()
        {
            if (fpOn) ExitFp();
            fpOn = false;
            GameMain.LuaCs.Hook.Remove("think", "fplite_update");
        }

        private static void HookPrivate(string name, LuaCsHook.HookMethodType type, Action<object> cb)
        {
            try
            {
                var m = typeof(Camera).GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
                if (m == null) { return; }
                GameMain.LuaCs.Hook.HookMethod("fplite_" + name, m,
                    (inst, _) => { cb(inst); return null; }, type);
            }
            catch (Exception e) { LogErr(e); }
        }

        private static void HookPublic(string name, Type[] sig, LuaCsHook.HookMethodType type, Action<object> cb)
        {
            try
            {
                var m = typeof(GameScreen).GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, sig, null);
                if (m == null) { return; }
                GameMain.LuaCs.Hook.HookMethod("fplite_" + name + sig.Length, m,
                    (inst, _) => { cb(inst); return null; }, type);
            }
            catch (Exception e) { LogErr(e); }
        }

        // =====================================================================
        // ВКЛ/ВЫКЛ
        // =====================================================================
        private static void EnterFp()
        {
            var cam = Screen.Selected?.Cam;
            if (cam != null) { savedCamPos = cam.Position; savedCamZoom = cam.Zoom; }
            try
            {
                savedLos = GameMain.LightManager.LosEnabled;
                savedLighting = GameMain.LightManager.LightingEnabled;
                GameMain.LightManager.LosEnabled = false;
            }
            catch { }

            var ch = Character.Controlled;
            lockedDir = ch?.AnimController?.Dir > 0 ? Direction.Right : Direction.Left;
            camYaw = lockedDir == Direction.Right ? 0f : MathF.PI;
            camPitch = 0f;
            fov = FovBase;
            flipCooldown = 0;

            CenterMouse();
            Log("FP ON");
        }

        private static void ExitFp()
        {
            var cam = Screen.Selected?.Cam;
            if (cam != null) { cam.Position = savedCamPos; cam.Zoom = savedCamZoom; }
            try
            {
                GameMain.LightManager.LosEnabled = savedLos;
                GameMain.LightManager.LightingEnabled = savedLighting;
            }
            catch { }
            Log("FP OFF");
        }

        // =====================================================================
        // КАМЕРА
        // =====================================================================
        private static Vector2 HeadPos
        {
            get
            {
                var ch = Character.Controlled;
                if (ch == null) { return Vector2.Zero; }
                try
                {
                    var limbs = ch.AnimController?.Limbs;
                    if (limbs != null)
                    {
                        foreach (var l in limbs)
                        {
                            if (l.type == LimbType.Head && l.body != null) { return l.WorldPosition; }
                        }
                    }
                }
                catch { }
                return ch.WorldPosition;
            }
        }

        private static void GetCameraVectors(out Vector3 pos, out Vector3 fwd)
        {
            Vector2 h = HeadPos;
            pos = new Vector3(h.X, h.Y, 0f);
            float cy = MathF.Cos(camYaw), sy = MathF.Sin(camYaw);
            float cp = MathF.Cos(camPitch), sp = MathF.Sin(camPitch);
            fwd = new Vector3(cy * cp, sp, sy * cp);
        }

        private static Matrix ViewProj
        {
            get
            {
                GetCameraVectors(out var pos, out var fwd);
                Matrix view = Matrix.CreateLookAt(pos, pos + fwd * 100f, Vector3.UnitY);
                Matrix proj = Matrix.CreatePerspectiveFieldOfView(
                    fov, (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight, 1f, 50000f);
                return view * proj;
            }
        }

        private static Matrix Screen
        {
            get
            {
                int sw = GameMain.GraphicsWidth, sh = GameMain.GraphicsHeight;
                return Matrix.CreateScale(sw / 2f, -sh / 2f, 1f) * Matrix.CreateTranslation(sw / 2f, sh / 2f, 0f);
            }
        }

        /// <summary>Стена на глубине z: квады в плоскости XY, сдвинутые по Z.</summary>
        private static Matrix WallMatrix(float z)
            => Matrix.CreateScale(1f, -1f, 1f) * Matrix.CreateTranslation(0f, 0f, z) * ViewProj * Screen;

        /// <summary>Пол/потолок: горизонтальная плоскость на высоте y.</summary>
        private static Matrix FloorMatrix(float y)
            => Matrix.CreateScale(1f, -1f, 1f) * Matrix.CreateRotationX(-MathF.PI / 2f)
               * Matrix.CreateTranslation(0f, y, 0f) * ViewProj * Screen;

        private static void OverrideTransform(Camera cam)
        {
            if (!fpOn || transformField == null) { return; }
            Matrix m = WallMatrix(0f);
            transformField.SetValue(cam, m);
            shaderTransformField?.SetValue(cam, m);
        }

        private static void PreDraw()
        {
            var cam = Screen.Selected?.Cam;
            if (cam == null) { return; }
            cam.Position = HeadPos;
            cam.Zoom = 0.01f;
            cam.Rotation = 0f;
        }

        // =====================================================================
        // ОБНОВЛЕНИЕ
        // =====================================================================
        private static void OnUpdate()
        {
            if (PlayerInput.KeyHit(Keys.F5))
            {
                fpOn = !fpOn;
                if (fpOn)
                {
                    if (Character.Controlled == null) { fpOn = false; return; }
                    EnsureResources();
                    EnterFp();
                }
                else { ExitFp(); }
            }

            if (PlayerInput.KeyHit(Keys.NumPad8))
            {
                limbThickness = (limbThickness + 1f) % 6f;
                Log("Limb thickness: " + limbThickness);
            }

            if (PlayerInput.KeyHit(Keys.NumPad2))
            {
                clipScale = clipScale >= 1.5f ? 0.6f : clipScale + 0.15f;
                Log("Room clip scale: " + clipScale.ToString("0.00"));
            }

            if (!fpOn || Character.Controlled == null) { return; }

            try { GameMain.LightManager.LosEnabled = false; } catch { }

            // --- мышь ---
            var ms = Mouse.GetState();
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
            camYaw = (camYaw + (ms.X - cx) * MouseSensitivity) % MathHelper.TwoPi;
            if (camYaw < 0) { camYaw += MathHelper.TwoPi; }
            camPitch = MathHelper.Clamp(camPitch - (ms.Y - cy) * MouseSensitivity * 0.7f,
                -MathF.PI / 2f + 0.1f, MathF.PI / 2f - 0.1f);
            CenterMouse();

            HandleMovement();

            // --- прицел: точка взгляда → CursorPosition (интеракт + аим) ---
            var ch = Character.Controlled;
            try { ch.CursorPosition = HeadPos + LookDir2D * InteractMaxDist; } catch { }

            if (flipCooldown > 0) { flipCooldown--; }
            SyncFacing(ch);
        }

        private static Vector2 LookDir2D
        {
            get
            {
                float cy = MathF.Cos(camYaw), cp = MathF.Cos(camPitch);
                return new Vector2(cy * cp, -MathF.Sin(camPitch) * cp);
            }
        }

        private static void CenterMouse() => Mouse.SetPosition(GameMain.GraphicsWidth / 2, GameMain.GraphicsHeight / 2);

        // =====================================================================
        // ДВИЖЕНИЕ: инъекция Keys.Held (персонаж идёт сам, сервер видит инпут)
        // =====================================================================
        private static void SyncFacing(Character ch)
        {
            if (ch?.AnimController == null) { return; }

            bool wantRight = MathF.Cos(camYaw) >= 0f;
            var anim = ch.AnimController;

            if (flipCooldown == 0 && (anim.Dir > 0) != wantRight && MathF.Abs(camPitch) < 0.9f)
            {
                try
                {
                    anim.TargetDir = wantRight ? Direction.Right : Direction.Left;
                    flipCooldown = 30;
                }
                catch { }
            }

            // курсор всегда по направлению взгляда — чтобы торс/руки доворачивались
            try
            {
                float d = wantRight ? 1f : -1f;
                ch.CursorPosition = ch.Position + new Vector2(d * 500f, -MathF.Sin(camPitch) * 300f);
            }
            catch { }
        }

        private static void HandleMovement()
        {
            var ch = Character.Controlled;
            if (ch?.AnimController == null || ch.Keys == null) { return; }

            var anim = ch.AnimController;

            if (flipCooldown > 0) { flipCooldown--; }

            // локальный хелпер: инъекция нажатия в инпут персонажа
            void Press(InputType t)
            {
                ch.Keys[(int)t].SetState(false, true);
                ch.Keys[(int)t].Held = true;
            }

            bool w = PlayerInput.KeyDown(Keys.W);
            bool s = PlayerInput.KeyDown(Keys.S);
            bool a = PlayerInput.KeyDown(Keys.A);
            bool d = PlayerInput.KeyDown(Keys.D);
            bool space = PlayerInput.KeyDown(Keys.Space);
            bool ctrl = PlayerInput.KeyDown(Keys.LeftControl);

            // сброс
            ch.Keys[(int)InputType.Left].Held = false;
            ch.Keys[(int)InputType.Right].Held = false;
            ch.Keys[(int)InputType.Up].Held = false;
            ch.Keys[(int)InputType.Down].Held = false;

            bool facingRight = lockedDir == Direction.Right;
            bool lookUp = camPitch > 0.3f;
            bool lookDown = camPitch < -0.3f;

            bool inWater;
            bool climbing;
            bool nearLadder;
            try { inWater = ch.InWater || anim.InWater == true; } catch { inWater = false; }
            try { climbing = anim.IsClimbing; } catch { climbing = false; }
            nearLadder = IsNearLadder(ch);

            // ===== ВОДА: куда смотришь — туда и плывёшь =====
            if (inWater)
            {
                GetCameraVectors(out _, out var fwd);
                Vector2 look = new(fwd.X, fwd.Y);

                Vector2 move = Vector2.Zero;
                if (w) { move += look; }
                if (s) { move -= look; }
                if (a || d)
                {
                    Vector2 right = new(-look.Y, look.X);
                    if (a) { move -= right; }
                    if (d) { move += right; }
                }
                if (space) { move.Y += 0.6f; }
                if (ctrl) { move.Y -= 0.6f; }

                if (move.LengthSquared() > 0.01f)
                {
                    move.Normalize();
                    if (MathF.Abs(move.Y) > MathF.Abs(move.X) * 1.2f)
                    {
                        // вертикаль доминирует → Up/Down
                        var key = move.Y > 0 ? InputType.Up : InputType.Down;
                        ch.Keys[(int)key].SetState(false, true);
                        ch.Keys[(int)key].Held = true;
                    }
                    else
                    {
                        // горизонталь → Right/Left по запертому направлению
                        bool forward = Vector2.Dot(move, new Vector2(facingRight ? 1f : -1f, 0)) > 0;
                        var key = forward == facingRight ? InputType.Right : InputType.Left;
                        ch.Keys[(int)key].SetState(false, true);
                        ch.Keys[(int)key].Held = true;
                    }
                }

                // камера «догоняет» взгляд
                anim.TargetDir = lockedDir;
                return;
            }

            // ===== ЛЕСТНИЦА: W+взгляд вверх = вверх, S+вниз = вниз =====
            if (climbing)
            {
                if (lookStraight())
                {
                    if (w) { Press(facingRight ? InputType.Right : InputType.Left); }
                    else if (s) { Press(facingRight ? InputType.Left : InputType.Right); }
                }
                else
                {
                    if (w) { Press(InputType.Up); }
                    else if (s) { Press(InputType.Down); }
                }
                return;
            }

            if (nearLadder && w && lookUp)
            {
                Press(InputType.Up);
                var ladder = FindNearestLadder(ch);
                try { ladder?.Select(ch); } catch { }
                return;
            }
            if (nearLadder && s && lookDown)
            {
                Press(InputType.Down);
                var ladder = FindNearestLadder(ch);
                try { ladder?.Select(ch); } catch { }
                return;
            }

            // ===== СУША: W/S = вперёд/назад по запертому направлению =====
            if (w)
            {
                Press(facingRight ? InputType.Right : InputType.Left);
                // взгляд вверх при ходьбе = попытка запрыгнуть
                if (lookUp) { Press(InputType.Up); }
            }
            else if (s)
            {
                Press(facingRight ? InputType.Left : InputType.Right);
            }
            if (space) { Press(InputType.Up); }         // прыжок
            if (ctrl) { Press(InputType.Crouch); }      // присед

        }

        private static bool lookStraight() => MathF.Abs(camPitch) <= 0.3f;

        // =====================================================================
        // ЛЕСТНИЦЫ
        // =====================================================================
        private static Ladder FindNearestLadder(Character ch)
        {
            Ladder best = null;
            float bestDist = 300f * 300f;
            try
            {
                foreach (var l in Ladder.List)
                {
                    if (l?.Item == null) { continue; }
                    float d = Vector2.DistanceSquared(ch.WorldPosition, l.Item.WorldPosition);
                    if (d < bestDist) { bestDist = d; best = l; }
                }
            }
            catch { }
            return best;
        }

        private static bool IsNearLadder(Character ch)
        {
            if (ch == null) { return false; }
            try
            {
                if (ch.AnimController?.IsClimbing == true) { return true; }
                foreach (var ladder in Ladder.List)
                {
                    if (ladder?.Item == null) { continue; }
                    float dx = MathF.Abs(ch.WorldPosition.X - ladder.Item.WorldPosition.X);
                    var wr = ladder.Item.WorldRect;
                    if (dx < 120f && ch.WorldPosition.Y <= wr.Y + 50f && ch.WorldPosition.Y >= wr.Y - wr.Height - 50f)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        // =====================================================================
        // КОМНАТА
        // =====================================================================
        private static void FindRoom(Vector2 pos)
        {
            currentHull = null;
            float bestDist = float.MaxValue;
            foreach (var hull in Hull.HullList)
            {
                try
                {
                    if (hull.Submarine == null) { continue; }
                    var wr = hull.WorldRect;
                    if (pos.X >= wr.X - 50f && pos.X <= wr.X + wr.Width + 50f &&
                        pos.Y <= wr.Y + 50f && pos.Y >= wr.Y - wr.Height - 50f)
                    {
                        float cx = wr.X + wr.Width * 0.5f, cy = wr.Y - wr.Height * 0.5f;
                        float d = Vector2.DistanceSquared(pos, new Vector2(cx, cy));
                        if (d < bestDist) { bestDist = d; currentHull = hull; }
                    }
                }
                catch { }
            }

            if (currentHull != null)
            {
                var wr = currentHull.WorldRect;
                roomLeft = wr.X;
                roomRight = wr.X + wr.Width;
                roomFloor = wr.Y - wr.Height;
                roomCeil = wr.Y;
            }
            else
            {
                roomLeft = pos.X - 400f * clipScale;
                roomRight = pos.X + 400f * clipScale;
                roomFloor = pos.Y - 200f * clipScale;
                roomCeil = pos.Y + 200f * clipScale;
            }
        }

        private static void RecacheWalls()
        {
            verticalWalls.Clear();
            horizontalWalls.Clear();
            doors.Clear();

            foreach (var s in Structure.WallList)
            {
                try
                {
                    if (s.Submarine == null || s.Remove) { continue; }
                    var wr = s.WorldRect;
                    // классификация: что длиннее — то и ориентация
                    if (wr.Width >= wr.Height) { horizontalWalls.Add(s); }
                    else { verticalWalls.Add(s); }
                }
                catch { }
            }

            foreach (var item in Item.ItemList)
            {
                try
                {
                    if (item.Remove || item.Submarine == null) { continue; }
                    if (item.HasTag(Tags.Door) || item.GetComponent<Door>() != null) { doors.Add(item); }
                }
                catch { }
            }
        }

        // =====================================================================
        // РЕНДЕР
        // =====================================================================
        private static void Draw3DWorld()
        {
            EnsureResources();

            var gd = GameMain.Instance.GraphicsDevice;
            Vector2 camPos = HeadPos;

            // пересбор кэша раз в 0.5с
            if (Timing.TotalTime > nextRecache)
            {
                nextRecache = Timing.TotalTime + 0.5;
                RecacheWalls();
            }

            FindRoom(camPos);

            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Color(11, 18, 26), 1f, 0);

            DrawRoomShell(camPos);
            DrawWalls(camPos);
            DrawDoors();
            DrawCharacters();
        }

        /// <summary>Пол/потолок/боковые стены комнаты — тайлом текстуры ближайшей структуры.</summary>
        private static void DrawRoomShell(Vector2 camPos)
        {
            // выбираем текстуры из ближайших горизонтальных/вертикальных структур
            Texture2D floorTex = null, wallTex = null;
            Rectangle floorSrc = default, wallSrc = default;
            Color floorTint = new(70, 66, 60), wallTint = new(74, 72, 68);

            float bestH = float.MaxValue, bestV = float.MaxValue;
            foreach (var s in horizontalWalls)
            {
                float d = Vector2.DistanceSquared(camPos, s.WorldPosition);
                if (d < bestH && GetTexture(s, out var t, out var src))
                {
                    bestH = d; floorTex = t; floorSrc = src;
                    floorTint = s.SpriteColor;
                }
            }
            foreach (var s in verticalWalls)
            {
                float d = Vector2.DistanceSquared(camPos, s.WorldPosition);
                if (d < bestV && GetTexture(s, out var t, out var src))
                {
                    bestV = d; wallTex = t; wallSrc = src;
                    wallTint = s.SpriteColor;
                }
            }

            var darkTint = new Color(
                (int)(floorTint.R * 0.75f), (int)(floorTint.G * 0.75f), (int)(floorTint.B * 0.75f));

            // ПОЛ
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearWrap,
                null, RasterizerState.CullNone, null, FloorMatrix(roomFloor));
            if (floorTex != null)
            {
                DrawTiledPlane(floorTex, floorSrc, roomLeft, roomRight, ZHalf, -ZHalf, roomFloor, darkTint, horizontal: true);
            }
            else
            {
                DrawQuadPlane(roomLeft, roomRight, ZHalf, -ZHalf, roomFloor, new Color(45, 42, 38));
            }
            fpBatch.End();

            // ПОТОЛОК
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearWrap,
                null, RasterizerState.CullNone, null, FloorMatrix(roomCeil));
            if (floorTex != null)
            {
                DrawTiledPlane(floorTex, floorSrc, roomLeft, roomRight, ZHalf, -ZHalf, roomCeil, darkTint, horizontal: true);
            }
            else
            {
                DrawQuadPlane(roomLeft, roomRight, ZHalf, -ZHalf, roomCeil, new Color(32, 36, 42));
            }
            fpBatch.End();

            // ЛЕВАЯ/ПРАВАЯ СТЕНЫ КОМНАТЫ
            foreach (var (x, tintW) in new[] { (roomLeft, wallTint), (roomRight, wallTint) })
            {
                fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearWrap,
                    null, RasterizerState.CullNone, null, SideMatrix(x));
                if (wallTex != null)
                {
                    DrawTiledPlane(wallTex, wallSrc, roomFloor, roomCeil, ZHalf, -ZHalf, x, tintW, horizontal: false);
                }
                else
                {
                    DrawQuadPlane(roomFloor, roomCeil, ZHalf, -ZHalf, x, new Color(56, 54, 50));
                }
                fpBatch.End();
            }
        }

        /// <summary>Боковая стена: вертикальная плоскость на координате x.</summary>
        private static Matrix SideMatrix(float x)
            => Matrix.CreateScale(1f, -1f, 1f) * Matrix.CreateRotationY(MathF.PI / 2f)
               * Matrix.CreateTranslation(x, 0f, 0f) * ViewProj * Screen;

        /// <summary>Залитый квад в плоскости (по X — от a до b, по Z — от z0 до z1) на высоте/позиции y.</summary>
        private static void DrawQuadPlane(float a, float b, float z0, float z1, float yOrX, Color color)
        {
            int sw = GameMain.GraphicsWidth, sh = GameMain.GraphicsHeight;
            var verts = new VertexPositionColorTexture[4];
            verts[0] = new(new Vector3(a, yOrX, z0), color, new Vector2(0, 0));
            verts[1] = new(new Vector3(b, yOrX, z0), color, new Vector2(1, 0));
            verts[2] = new(new Vector3(a, yOrX, z1), color, new Vector2(0, 1));
            verts[3] = new(new Vector3(b, yOrX, z1), color, new Vector2(1, 1));

            SetupBasicEffect();
            basicEffect.TextureEnabled = false;
            foreach (var pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                GameMain.Instance.GraphicsDevice.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleStrip, verts, 0, 4, QuadIndices, 0, 2);
            }
        }

        /// <summary>Тайлинг текстуры по плоскости (X-плоскость для пола, Y — для стен).</summary>
        private static void DrawTiledPlane(Texture2D tex, Rectangle src,
            float a, float b, float z0, float z1, float yOrX, Color tint, bool horizontal)
        {
            float tileWorld = 128f;                       // мировая ширина тайла
            int tiles = Math.Max(1, (int)MathF.Ceiling((b - a) / tileWorld));
            float tileLen = (b - a) / tiles;

            var gd = GameMain.Instance.GraphicsDevice;
            SetupBasicEffect();
            basicEffect.Texture = tex;
            basicEffect.TextureEnabled = true;

            var verts = new VertexPositionColorTexture[4];
            for (int i = 0; i < tiles; i++)
            {
                float a0 = a + i * tileLen, a1 = a0 + tileLen;
                float u0 = (i % 2 == 0) ? 0f : 1f;         // чередуем зеркаление — без швов
                float u1 = 1f - u0;

                if (horizontal)
                {
                    verts[0] = new(new Vector3(a0, yOrX, z0), tint, new Vector2(u0, 0));
                    verts[1] = new(new Vector3(a1, yOrX, z0), tint, new Vector2(u1, 0));
                    verts[2] = new(new Vector3(a0, yOrX, z1), tint, new Vector2(u0, 1));
                    verts[3] = new(new Vector3(a1, yOrX, z1), tint, new Vector2(u1, 1));
                }
                else
                {
                    verts[0] = new(new Vector3(yOrX, a0, z0), tint, new Vector2(u0, 0));
                    verts[1] = new(new Vector3(yOrX, a1, z0), tint, new Vector2(u1, 0));
                    verts[2] = new(new Vector3(yOrX, a0, z1), tint, new Vector2(u0, 1));
                    verts[3] = new(new Vector3(yOrX, a1, z1), tint, new Vector2(u1, 1));
                }

                foreach (var pass in basicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleStrip, verts, 0, 4, QuadIndices, 0, 2);
                }
            }
        }

        private static readonly short[] QuadIndices = { 0, 1, 2, 2, 1, 3 };

        private static void SetupBasicEffect()
        {
            if (basicEffect == null)
            {
                basicEffect = new BasicEffect(GameMain.Instance.GraphicsDevice);
            }
            GetCameraVectors(out var pos, out var fwd);
            basicEffect.View = Matrix.CreateLookAt(pos, pos + fwd * 100f, Vector3.UnitY);
            basicEffect.Projection = Matrix.CreatePerspectiveFieldOfView(
                fov, (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight, 1f, 50000f);
            basicEffect.World = Matrix.Identity;
            basicEffect.VertexColorEnabled = true;
        }

        /// <summary>Стены суба: рисуются биллбордами на их Z из SpriteDepth (ближние слои).</summary>
        private static void DrawWalls(Vector2 camPos)
        {
            fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied,
                null, null, RasterizerState.CullNone, null, WallMatrix(0f));

            float maxDist = 3500f;
            foreach (var s in verticalWalls)
            {
                if (!InRoom(s.WorldPosition, camPos, maxDist)) { continue; }
                s.Draw(fpBatch, false, true);
            }
            foreach (var s in horizontalWalls)
            {
                if (!InRoom(s.WorldPosition, camPos, maxDist)) { continue; }
                s.Draw(fpBatch, false, true);
            }

            fpBatch.End();
        }

        private static void DrawDoors()
        {
            if (doors.Count == 0) { return; }
            fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied,
                null, null, RasterizerState.CullNone, null, WallMatrix(0f));
            foreach (var d in doors)
            {
                try
                {
                    if (Vector2.DistanceSquared(HeadPos, d.WorldPosition) > 3500f * 3500f) { continue; }
                    d.Draw(fpBatch, false, true);
                }
                catch { }
            }
            fpBatch.End();
        }

        private static bool InRoom(Vector2 p, Vector2 camPos, float maxDist)
        {
            if (xrayStyleClip) { return Vector2.DistanceSquared(p, camPos) <= maxDist * maxDist; }
            float pad = 120f * clipScale;
            return p.X >= roomLeft - pad && p.X <= roomRight + pad &&
                   p.Y >= roomFloor - pad && p.Y <= roomCeil + pad &&
                   Vector2.DistanceSquared(p, camPos) <= maxDist * maxDist;
        }

        private static bool xrayStyleClip;

        // =====================================================================
        // ПЕРСОНАЖИ: биллборды на Z чуть впереди стен (+ опц. объём лимбов)
        // =====================================================================
        private static void DrawCharacters()
        {
            float zChar = MathF.Min(ZHalf - 10f, ZHalf * 0.9f);

            foreach (var c in Character.CharacterList)
            {
                try
                {
                    if (c.Removed || c == Character.Controlled) { continue; }
                    if (!InRoom(c.WorldPosition, HeadPos, 4000f)) { continue; }

                    if (limbThickness <= 0f)
                    {
                        // плоский биллборд
                        fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied,
                            null, null, RasterizerState.CullNone, null, WallMatrix(zChar));
                        c.Draw(fpBatch, Screen.Selected?.Cam);
                        fpBatch.End();
                    }
                    else
                    {
                        DrawCharacterExtruded(c, zChar);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Объёмный персонаж: каждый лимб рисуется дважды (перед/зад) с
        /// соединительными рёбрами по краям — простая «коробка» без мешей.
        /// </summary>
        private static void DrawCharacterExtruded(Character c, float zCenter)
        {
            var limbs = c.AnimController?.Limbs;
            if (limbs == null) { return; }

            float half = limbThickness * 0.5f + 2f;
            float zFront = zCenter + half;
            float zBack = zCenter - half;

            var gd = GameMain.Instance.GraphicsDevice;
            SetupBasicEffect();

            foreach (var limb in limbs)
            {
                try
                {
                    if (limb?.body == null) { continue; }
                    var sp = limb.ActiveSprite;
                    if (sp == null) { continue; }

                    var pos = ConvertUnits.ToDisplayUnits(limb.body.Position);
                    float rot = limb.body.Rotation;
                    bool flip = c.AnimController?.Dir < 0;

                    float w = sp.SourceRect.Width;
                    float h = sp.SourceRect.Height;

                    Color col = Color.White;
                    

                    var cFront = col;
                    var cBack = new Color(col.R / 2, col.G / 2, col.B / 2);
                    var cSide = new Color(col.R * 3 / 4, col.G * 3 / 4, col.B * 3 / 4);

                    float hw = w * 0.5f, hh = h * 0.5f;

                    // 4 угла (локальные), потом поворот
                    Vector2 Rotate(Vector2 v)
                    {
                        float cs = MathF.Cos(rot), sn = MathF.Sin(rot);
                        return new Vector2(v.X * cs - v.Y * sn, v.X * sn + v.Y * cs);
                    }

                    var tl = Rotate(new Vector2(-hw, -hh)) + pos;
                    var tr = Rotate(new Vector2(hw, -hh)) + pos;
                    var bl = Rotate(new Vector2(-hw, hh)) + pos;
                    var br = Rotate(new Vector2(hw, hh)) + pos;

                    var verts = new VertexPositionColorTexture[8];
                    verts[0] = new(new Vector3(tl, zFront), cFront, new Vector2(0, 0));
                    verts[1] = new(new Vector3(tr, zFront), cFront, new Vector2(1, 0));
                    verts[2] = new(new Vector3(bl, zFront), cFront, new Vector2(0, 1));
                    verts[3] = new(new Vector3(br, zFront), cFront, new Vector2(1, 1));
                    verts[4] = new(new Vector3(tl, zBack), cBack, new Vector2(0, 0));
                    verts[5] = new(new Vector3(tr, zBack), cBack, new Vector2(1, 0));
                    verts[6] = new(new Vector3(bl, zBack), cBack, new Vector2(0, 1));
                    verts[7] = new(new Vector3(br, zBack), cBack, new Vector2(1, 1));

                    // перед и зад
                    short[] frontIdx = { 0, 1, 2, 2, 1, 3 };
                    short[] backIdx = { 4, 6, 5, 5, 6, 7 };
                    short[] sideIdx =
                    {
                        0, 4, 1, 1, 4, 5,   // верх
                        2, 3, 6, 6, 3, 7,   // низ
                        0, 2, 4, 4, 2, 6,   // лево
                        1, 5, 3, 3, 5, 7    // право
                    };

                    if (sp.Texture != null)
                    {
                        basicEffect.Texture = sp.Texture;
                        basicEffect.TextureEnabled = true;
                    }
                    else
                    {
                        basicEffect.TextureEnabled = false;
                    }

                    foreach (var pass in basicEffect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, 8, backIdx, 0, 4);
                        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, 8, sideIdx, 0, 8);
                        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, 8, frontIdx, 0, 2);
                    }
                }
                catch { }
            }
        }

        // =====================================================================
        // OVERLAY
        // =====================================================================
        private static void DrawOverlay()
        {
            EnsureResources();
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;

            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);

            var col = Color.White * 0.8f;
            fpBatch.Draw(pixelTex, new Rectangle(cx - 12, cy, 8, 2), col);
            fpBatch.Draw(pixelTex, new Rectangle(cx + 4, cy, 8, 2), col);
            fpBatch.Draw(pixelTex, new Rectangle(cx, cy - 12, 2, 8), col);
            fpBatch.Draw(pixelTex, new Rectangle(cx, cy + 4, 2, 8), col);

            fpBatch.Draw(pixelTex, new Rectangle(10, 10, 250, 46), new Color(0, 0, 0, 170));
            GUI.DrawString(fpBatch, new Vector2(18, 16),
                $"F5 FP | limbs: {limbThickness:0} | clip: {clipScale:0.00}", Color.LightGreen);
            GUI.DrawString(fpBatch, new Vector2(18, 34), "E/LMB use | NumPad8/2 settings", Color.Gray);

            fpBatch.End();
        }

        // =====================================================================
        // РЕСУРСЫ/УТИЛИТЫ
        // =====================================================================
        private static void EnsureResources()
        {
            var gd = GameMain.Instance.GraphicsDevice;
            if (pixelTex == null || pixelTex.IsDisposed)
            {
                pixelTex = new Texture2D(gd, 1, 1);
                pixelTex.SetData(new[] { Color.White });
            }
            if (fpBatch == null || fpBatch.IsDisposed)
            {
                fpBatch = new SpriteBatch(gd);
            }
        }

        /// <summary>Достаём текстуру и source-прямоугольник структуры.</summary>
        private static bool GetTexture(Structure s, out Texture2D tex, out Rectangle src)
        {
            tex = null; src = default;
            try
            {
                var spr = s.Sprite;
                if (spr == null) { return false; }
                tex = spr.Texture;
                src = spr.SourceRect;
                return tex != null && src.Width > 0 && src.Height > 0;
            }
            catch { return false; }
        }

        private static void Log(string m) => DebugConsole.NewMessage("[FPLite] " + m, Color.Cyan);
        private static void LogErr(Exception e) => DebugConsole.NewMessage("[FPLite] ERR: " + e.Message, Color.Red);
    }
}
