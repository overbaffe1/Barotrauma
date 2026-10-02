using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FirstPersonMod
{
    public class True3DMod : ACsMod
    {
        // ===== НАСТРОЙКИ =====
        private const float MOUSE_SENSITIVITY = 0.003f;
        private const float MOVE_SPEED = 8f;
        private const float FOV_BASE = MathF.PI / 2.5f;

        // ===== СОСТОЯНИЕ =====
        private static bool isEnabled = false;
        private static float camYaw = 0f;
        private static float camPitch = 0f;
        private static Vector2 camPos = Vector2.Zero;
        private static float fov = FOV_BASE;
        private static int renderMode = 0;
        private static int prevScrollValue = 0;

        // ===== СОХРАНЁННОЕ =====
        private static Vector2 savedCamPos;
        private static float savedCamZoom;
        private static bool savedLos;
        private static bool savedLighting;

        // ===== РЕСУРСЫ =====
        private static Texture2D pixelTex;
        private static SpriteBatch fpBatch;
        private static FieldInfo transformField;
        private static FieldInfo shaderTransformField;
        private static RenderTarget2D[] depthTargets;
        private static Color[] rayBuffer;
        private static Texture2D rayTexture;

        // ===== 8 НОВЫХ РЕЖИМОВ =====
        private static readonly string[] MODE_NAMES = new[]
        {
            "1: True Raycast Walls",
            "2: 3D Billboard Space",
            "3: Hull Room 3D",
            "4: Wall Extrusion",
            "5: Depth Sphere",
            "6: Column Renderer",
            "7: Layered Depth",
            "8: Voxel Convert"
        };

        // =====================================================================
        // КОНСТРУКТОР
        // =====================================================================
        public True3DMod()
        {
            transformField = typeof(Camera).GetField("transform", BindingFlags.NonPublic | BindingFlags.Instance);
            shaderTransformField = typeof(Camera).GetField("shaderTransform", BindingFlags.NonPublic | BindingFlags.Instance);

            GameMain.LuaCs.Hook.Add("think", "true3d_update", (object[] args) =>
            {
                try { OnUpdate(); } catch (Exception e) { LogErr(e); }
                return null;
            });

            try
            {
                var utm = typeof(Camera).GetMethod("UpdateTransform", BindingFlags.Public | BindingFlags.Instance);
                if (utm != null)
                    GameMain.LuaCs.Hook.HookMethod("true3d_transform", utm,
                    (object inst, Dictionary<string, object> a) =>
                    {
                        if (isEnabled && Character.Controlled != null)
                            try { OverrideTransform((Camera)inst); } catch { }
                        return null;
                    }, LuaCsHook.HookMethodType.Before);

                var dm = typeof(GameScreen).GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(double), typeof(GraphicsDevice), typeof(SpriteBatch) }, null);
                if (dm != null)
                {
                    GameMain.LuaCs.Hook.HookMethod("true3d_predraw", dm,
                    (object inst, Dictionary<string, object> a) =>
                    {
                        if (isEnabled && Character.Controlled != null) PreDraw();
                        return null;
                    }, LuaCsHook.HookMethodType.Before);

                    GameMain.LuaCs.Hook.HookMethod("true3d_postdraw", dm,
                    (object inst, Dictionary<string, object> a) =>
                    {
                        if (isEnabled && Character.Controlled != null) DrawOverlay();
                        return null;
                    }, LuaCsHook.HookMethodType.After);
                }

                var dmm = typeof(GameScreen).GetMethod("DrawMap", BindingFlags.Public | BindingFlags.Instance);
                if (dmm != null)
                    GameMain.LuaCs.Hook.HookMethod("true3d_drawmap", dmm,
                    (object inst, Dictionary<string, object> a) =>
                    {
                        if (isEnabled && Character.Controlled != null)
                            try { Render3DWorld(); } catch (Exception ex) { LogErr(ex); }
                        return null;
                    }, LuaCsHook.HookMethodType.After);
            }
            catch (Exception e) { LogErr(e); }

            Log("True 3D Mod Loaded! | F5=Toggle | F2=Switch Mode | Mouse=Look | WASD=Move");
        }

        public override void Stop()
        {
            if (isEnabled) Disable();
            isEnabled = false;
            GameMain.LuaCs.Hook.Remove("think", "true3d_update");
            CleanupResources();
        }

        // =====================================================================
        // УТИЛИТЫ
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
                fpBatch = new SpriteBatch(gd);
        }

        private static void EnsureRenderTargets()
        {
            var gd = GameMain.Instance.GraphicsDevice;
            int w = GameMain.GraphicsWidth, h = GameMain.GraphicsHeight;

            if (depthTargets == null || depthTargets.Length < 8)
            {
                CleanupRenderTargets();
                depthTargets = new RenderTarget2D[8];
                for (int i = 0; i < 8; i++)
                    depthTargets[i] = new RenderTarget2D(gd, w / 4, h / 4, false, SurfaceFormat.Color, DepthFormat.None);
            }

            if (rayBuffer == null || rayBuffer.Length != w * h / 16)
            {
                rayBuffer = new Color[w * h / 16];
                if (rayTexture != null) rayTexture.Dispose();
                rayTexture = new Texture2D(gd, w / 4, h / 4);
            }
        }

        private static void CleanupRenderTargets()
        {
            if (depthTargets != null)
                foreach (var rt in depthTargets) rt?.Dispose();
            rayTexture?.Dispose();
        }

        private static void CleanupResources()
        {
            CleanupRenderTargets();
            pixelTex?.Dispose();
            fpBatch?.Dispose();
        }

        private static void Enable()
        {
            var cam = Screen.Selected?.Cam;
            if (cam != null)
            {
                savedCamPos = cam.Position;
                savedCamZoom = cam.Zoom;
            }
            try
            {
                savedLos = GameMain.LightManager.LosEnabled;
                savedLighting = GameMain.LightManager.LightingEnabled;
                GameMain.LightManager.LosEnabled = false;
                GameMain.LightManager.LightingEnabled = false;
            } catch { }

            camPos = GetHeadPos();
            camYaw = Character.Controlled.AnimController?.Dir > 0 ? 0f : MathF.PI;
            camPitch = 0f;
            fov = FOV_BASE;

            Mouse.SetPosition(GameMain.GraphicsWidth / 2, GameMain.GraphicsHeight / 2);
            Log("3D ON - " + MODE_NAMES[renderMode]);
        }

        private static void Disable()
        {
            var cam = Screen.Selected?.Cam;
            if (cam != null)
            {
                cam.Position = savedCamPos;
                cam.Zoom = savedCamZoom;
            }
            try
            {
                GameMain.LightManager.LosEnabled = savedLos;
                GameMain.LightManager.LightingEnabled = savedLighting;
            } catch { }
            Log("3D OFF");
        }

        private static Vector2 GetHeadPos()
        {
            var ch = Character.Controlled;
            if (ch == null) return Vector2.Zero;
            try
            {
                if (ch.AnimController?.Limbs != null)
                    foreach (var l in ch.AnimController.Limbs)
                        if (l.type == LimbType.Head && l.body != null)
                            return l.WorldPosition;
            } catch { }
            return ch.WorldPosition;
        }

        private static Vector2 GetLookDir()
        {
            float cy = MathF.Cos(camYaw), sy = MathF.Sin(camYaw);
            float cp = MathF.Cos(camPitch), sp = MathF.Sin(camPitch);
            return new Vector2(cy * cp, -sp * cp);
        }

        private static Vector3 GetForwardDir3D()
        {
            float cy = MathF.Cos(camYaw), sy = MathF.Sin(camYaw);
            float cp = MathF.Cos(camPitch), sp = MathF.Sin(camPitch);
            return new Vector3(cy * cp, sp, sy * cp);
        }

        private static void Log(string m) => DebugConsole.NewMessage("[True3D] " + m, Color.Cyan);
        private static void LogErr(Exception e) => DebugConsole.NewMessage("[True3D] ERR: " + e.Message, Color.Red);

        // =====================================================================
        // МАТРИЦЫ
        // =====================================================================
        private static Matrix GetPerspectiveMatrix()
        {
            float aspect = (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight;
            Vector3 pos = new Vector3(camPos.X, camPos.Y, 0f);
            Vector3 fwd = GetForwardDir3D();
            Vector3 target = pos + fwd * 100f;

            Matrix view = Matrix.CreateLookAt(pos, target, Vector3.UnitY);
            Matrix proj = Matrix.CreatePerspectiveFieldOfView(fov, aspect, 10f, 50000f);
            Matrix screenScale = Matrix.CreateScale(GameMain.GraphicsWidth / 2f, -GameMain.GraphicsHeight / 2f, 1f);
            Matrix screenTrans = Matrix.CreateTranslation(GameMain.GraphicsWidth / 2f, GameMain.GraphicsHeight / 2f, 0f);

            return view * proj * screenScale * screenTrans;
        }

        private static void OverrideTransform(Camera cam)
        {
            if (!isEnabled || transformField == null) return;
            Matrix matrix = GetPerspectiveMatrix();
            transformField.SetValue(cam, matrix);
            shaderTransformField?.SetValue(cam, matrix);
        }

        private static void PreDraw()
        {
            var cam = Screen.Selected?.Cam;
            if (cam == null) return;
            cam.Position = camPos;
            cam.Zoom = 1.0f;
            cam.Rotation = 0f;
        }

        // =====================================================================
        // УПРАВЛЕНИЕ (F5 ВКЛЮЧЕНИЕ!)
        // =====================================================================
        private static void OnUpdate()
        {
            // F5 = ВКЛ/ВЫКЛ (как ты хотел!)
            if (PlayerInput.KeyHit(Keys.F5))
            {
                isEnabled = !isEnabled;
                if (isEnabled)
                {
                    if (Character.Controlled == null) { isEnabled = false; return; }
                    EnsureResources();
                    EnsureRenderTargets();
                    Enable();
                }
                else { Disable(); }
            }

            // F2 = Смена режима
            if (PlayerInput.KeyHit(Keys.F2))
            {
                renderMode = (renderMode + 1) % MODE_NAMES.Length;
                Log(MODE_NAMES[renderMode]);
            }

            if (!isEnabled || Character.Controlled == null) return;

            try { GameMain.LightManager.LosEnabled = false; GameMain.LightManager.LightingEnabled = false; } catch { }

            var ms = Mouse.GetState();
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;
            camYaw += (ms.X - cx) * MOUSE_SENSITIVITY;
            camYaw %= MathF.PI * 2f; if (camYaw < 0) camYaw += MathF.PI * 2f;
            camPitch = MathHelper.Clamp(camPitch - (ms.Y - cy) * MOUSE_SENSITIVITY * 0.7f, -MathF.PI / 2f + 0.1f, MathF.PI / 2f - 0.1f);
            Mouse.SetPosition(cx, cy);

            UpdateCursor();

            Vector2 move = Vector2.Zero;
            Vector2 lookDir = GetLookDir();
            Vector2 right = new Vector2(-lookDir.Y, lookDir.X);

            if (PlayerInput.KeyDown(Keys.W)) move += lookDir;
            if (PlayerInput.KeyDown(Keys.S)) move -= lookDir;
            if (PlayerInput.KeyDown(Keys.A)) move -= right;
            if (PlayerInput.KeyDown(Keys.D)) move += right;
            if (PlayerInput.KeyDown(Keys.Space)) move.Y += 1f;
            if (PlayerInput.KeyDown(Keys.LeftControl)) move.Y -= 1f;

            if (move.LengthSquared() > 0.01f)
            {
                move.Normalize();
                camPos += move * MOVE_SPEED;
            }

            int sd = ms.ScrollWheelValue - prevScrollValue;
            prevScrollValue = ms.ScrollWheelValue;
            if (sd != 0) fov = MathHelper.Clamp(fov - sd * 0.001f, 0.5f, 2.5f);

            var cam = Screen.Selected?.Cam;
            if (cam != null)
            {
                cam.Position = camPos;
                cam.Rotation = 0f;
            }
        }

        private static void UpdateCursor()
        {
            var ch = Character.Controlled;
            if (ch == null) return;

            Vector2 lookDir = GetLookDir();
            Vector2 cursorWorldPos = camPos + lookDir * 500f;
            ch.CursorPosition = cursorWorldPos;

            if (ch.AnimController != null)
            {
                bool faceLeft = MathF.Cos(camYaw) < 0;
                if ((ch.AnimController.Dir > 0) == faceLeft)
                    ch.AnimController.Flip();
            }
        }

        // =====================================================================
        // ГЛАВНЫЙ РЕНДЕР
        // =====================================================================
        private static void Render3DWorld()
        {
            EnsureResources();
            var cam = Screen.Selected?.Cam;
            if (cam == null) return;

            switch (renderMode)
            {
                case 0: RenderMode_RaycastWalls(cam); break;
                case 1: RenderMode_Billboard3D(cam); break;
                case 2: RenderMode_HullRoom3D(cam); break;
                case 3: RenderMode_WallExtrusion(cam); break;
                case 4: RenderMode_DepthSphere(cam); break;
                case 5: RenderMode_ColumnRenderer(cam); break;
                case 6: RenderMode_LayeredDepth(cam); break;
                case 7: RenderMode_VoxelConvert(cam); break;
            }
        }

        // =====================================================================
        // РЕЖИМ 1: TRUE RAYCAST WALLS
        // Стреляем лучами, находим РЕАЛЬНЫЕ стены Structure, рисуем 3D столбцы
        // =====================================================================
        private static void RenderMode_RaycastWalls(Camera cam)
        {
            int sw = GameMain.GraphicsWidth, sh = GameMain.GraphicsHeight;
            int scale = 2;
            int rw = sw / scale, rh = sh / scale;

            if (rayBuffer == null || rayBuffer.Length != rw * rh)
            {
                rayBuffer = new Color[rw * rh];
                if (rayTexture != null) rayTexture.Dispose();
                rayTexture = new Texture2D(GameMain.Instance.GraphicsDevice, rw, rh);
            }

            Array.Fill(rayBuffer, new Color(11, 18, 26, 255));

            float halfFov = fov / 2f;
            Vector3 fwd = GetForwardDir3D();
            Vector3 camPos3 = new Vector3(camPos.X, camPos.Y, 0);

            // Собираем все стены
            var walls = new List<(Rectangle rect, Color color, Structure struct)>();
            foreach (var s in Structure.WallList)
            {
                if (s.Submarine == null) continue;
                walls.Add((s.WorldRect, s.SpriteColor, s));
            }

            for (int py = 0; py < rh; py++)
            {
                for (int px = 0; px < rw; px++)
                {
                    float ndcX = (px / (float)rw - 0.5f) * 2f;
                    float rayAngle = camYaw + MathF.Atan2(ndcX * MathF.Tan(halfFov), 1f);
                    Vector2 rayDir = new Vector2(MathF.Cos(rayAngle), MathF.Sin(rayAngle));

                    float closestDist = float.MaxValue;
                    Color hitColor = new Color(11, 18, 26, 255);

                    foreach (var (rect, color, _) in walls)
                    {
                        // Пересечение луча с прямоугольником
                        float? hitDist = RaycastRect(camPos, rayDir, rect);
                        if (hitDist.HasValue && hitDist.Value < closestDist && hitDist.Value > 10f)
                        {
                            closestDist = hitDist.Value;
                            float brightness = MathHelper.Clamp(1f - closestDist / 3000f, 0.2f, 1f);
                            hitColor = new Color(
                                (int)(color.R * brightness),
                                (int)(color.G * brightness),
                                (int)(color.B * brightness), 255);
                        }
                    }

                    if (closestDist < float.MaxValue - 1)
                    {
                        // Высота стены на экране
                        float wallHeight = 200f;
                        float projectedHeight = (wallHeight / closestDist) * (rh / (2f * MathF.Tan(halfFov)));
                        int drawTop = (int)(rh / 2f - projectedHeight / 2f);
                        int drawBot = (int)(rh / 2f + projectedHeight / 2f);

                        for (int row = Math.Max(0, drawTop); row < Math.Min(rh, drawBot); row++)
                        {
                            rayBuffer[row * rw + px] = hitColor;
                        }
                    }
                }
            }

            rayTexture.SetData(rayBuffer);

            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp);
            fpBatch.Draw(rayTexture, new Rectangle(0, 0, sw, sh), Color.White);
            fpBatch.End();

            // Персонажи поверх
            DrawCharacters(cam);
        }

        private static float? RaycastRect(Vector2 origin, Vector2 dir, Rectangle rect)
        {
            // Упрощённое пересечение луча с AABB
            float tmin = (rect.X - origin.X) / dir.X;
            float tmax = (rect.X + rect.Width - origin.X) / dir.X;
            if (tmin > tmax) { float t = tmin; tmin = tmax; tmax = t; }

            float tymin = (rect.Y - rect.Height - origin.Y) / dir.Y;
            float tymax = (rect.Y - origin.Y) / dir.Y;
            if (tymin > tymax) { float t = tymin; tymin = tymax; tymax = t; }

            if ((tmin > tymax) || (tymin > tmax)) return null;
            if (tymin > tmin) tmin = tymin;
            if (tymax < tmax) tmax = tymax;

            return tmin > 10f ? tmin : (tmax > 10f ? tmax : null);
        }

        // =====================================================================
        // РЕЖИМ 2: 3D BILLBOARD SPACE
        // Все объекты в 3D пространстве, масштаб по дистанции, сортировка
        // =====================================================================
        private static void RenderMode_Billboard3D(Camera cam)
        {
            var drawList = new List<(MapEntity entity, float dist, Vector3 pos3D)>();

            foreach (var s in Structure.WallList)
            {
                if (s.Submarine == null) continue;
                Vector3 pos = new Vector3(s.WorldPosition.X, s.WorldPosition.Y, 0);
                float dist = Vector3.Distance(new Vector3(camPos.X, camPos.Y, 0), pos);
                if (dist < 4000)
                    drawList.Add((s, dist, pos));
            }

            foreach (var item in Item.ItemList)
            {
                if (item.ParentInventory == null && !item.Removed)
                {
                    Vector3 pos = new Vector3(item.WorldPosition.X, item.WorldPosition.Y, 0);
                    float dist = Vector3.Distance(new Vector3(camPos.X, camPos.Y, 0), pos);
                    if (dist < 4000)
                        drawList.Add((item, dist, pos));
                }
            }

            drawList.Sort((a, b) => b.dist.CompareTo(a.dist));

            Matrix projMatrix = GetPerspectiveMatrix();
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, null, null, null, projMatrix);

            foreach (var (entity, dist, pos3D) in drawList)
            {
                float scale = 1000f / Math.Max(dist, 100f);
                if (entity is Structure s)
                {
                    s.Draw(fpBatch, false, true);
                }
                else if (entity is Item it)
                {
                    it.Draw(fpBatch, false, true);
                }
            }

            fpBatch.End();
            DrawCharacters(cam);
        }

        // =====================================================================
        // РЕЖИМ 3: HULL ROOM 3D
        // Используем Hull данные для создания 3D комнаты с полом/потолком/стенами
        // =====================================================================
        private static void RenderMode_HullRoom3D(Camera cam)
        {
            Hull currentHull = Hull.FindHull(camPos);
            Matrix projMatrix = GetPerspectiveMatrix();

            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, null, null, null, projMatrix);

            if (currentHull != null)
            {
                // Пол
                Color floorColor = new Color(55, 50, 45, 255);
                fpBatch.Draw(pixelTex, 
                    new Rectangle(currentHull.Rect.X, currentHull.Rect.Y - currentHull.Rect.Height, currentHull.Rect.Width, 20), 
                    floorColor);

                // Потолок
                Color ceilColor = new Color(40, 45, 55, 255);
                fpBatch.Draw(pixelTex, 
                    new Rectangle(currentHull.Rect.X, currentHull.Rect.Y, currentHull.Rect.Width, 20), 
                    ceilColor);

                // Стены по краям Hull
                Color wallColor = new Color(60, 58, 55, 255);
                fpBatch.Draw(pixelTex, 
                    new Rectangle(currentHull.Rect.X, currentHull.Rect.Y - currentHull.Rect.Height, 20, currentHull.Rect.Height), 
                    wallColor);
                fpBatch.Draw(pixelTex, 
                    new Rectangle(currentHull.Rect.X + currentHull.Rect.Width - 20, currentHull.Rect.Y - currentHull.Rect.Height, 20, currentHull.Rect.Height), 
                    wallColor);
            }

            fpBatch.End();
            DrawCharacters(cam);
        }

        // =====================================================================
        // РЕЖИМ 4: WALL EXTRUSION
        // 2D стены "выдавливаются" в 3D через множественные слои с смещением
        // =====================================================================
        private static void RenderMode_WallExtrusion(Camera cam)
        {
            Matrix projMatrix = GetPerspectiveMatrix();
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, null, null, null, projMatrix);

            foreach (var s in Structure.WallList)
            {
                if (s.Submarine == null) continue;
                if (Vector2.Distance(camPos, s.WorldPosition) > 3000) continue;

                int layers = 5;
                for (int i = 0; i < layers; i++)
                {
                    float offset = i * 8f;
                    float brightness = 1.0f - (i / (float)layers) * 0.4f;

                    Matrix extrudeMatrix = Matrix.CreateTranslation(offset, 0, 0) * GetPerspectiveMatrix();
                    fpBatch.End();
                    fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, null, null, null, extrudeMatrix);

                    Color tint = s.SpriteColor * brightness;
                    s.Draw(fpBatch, false, true);
                }
            }

            fpBatch.End();
            DrawCharacters(cam);
        }

        // =====================================================================
        // РЕЖИМ 5: DEPTH SPHERE
        // Оборачиваем мир вокруг сферы, эффект рыбьего глаза 360°
        // =====================================================================
        private static void RenderMode_DepthSphere(Camera cam)
        {
            float sphereRadius = 2000f;
            int segments = 16;

            var gd = GameMain.Instance.GraphicsDevice;
            gd.SetRenderTarget(null);
            gd.Clear(new Color(11, 18, 26, 255));

            for (int i = 0; i < segments; i++)
            {
                float angleY = (i / (float)segments) * MathF.PI * 2f;
                float angleP = camPitch * 0.5f;

                Matrix sphereTransform =
                    Matrix.CreateRotationY(angleY) *
                    Matrix.CreateRotationX(angleP) *
                    Matrix.CreateTranslation(0, 0, sphereRadius) *
                    GetPerspectiveMatrix();

                fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, null, null, null, sphereTransform);

                foreach (var s in Structure.WallList)
                {
                    if (s.Submarine == null) continue;
                    if (Vector2.Distance(camPos, s.WorldPosition) < 3000)
                        s.Draw(fpBatch, false, true);
                }

                fpBatch.End();
            }

            DrawCharacters(cam);
        }

        // =====================================================================
        // РЕЖИМ 6: COLUMN RENDERER
        // Wolfenstein-стиль: каждый столбец экрана = луч, стена = вертикальная полоса
        // =====================================================================
        private static void RenderMode_ColumnRenderer(Camera cam)
        {
            int sw = GameMain.GraphicsWidth, sh = GameMain.GraphicsHeight;
            float halfFov = fov / 2f;

            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque);

            var walls = new List<(Rectangle rect, Color color)>();
            foreach (var s in Structure.WallList)
            {
                if (s.Submarine == null) continue;
                walls.Add((s.WorldRect, s.SpriteColor));
            }

            for (int col = 0; col < sw; col += 4)
            {
                float screenX = (col / (float)sw - 0.5f) * 2f;
                float rayAngle = camYaw + MathF.Atan2(screenX * MathF.Tan(halfFov), 1f);
                Vector2 rayDir = new Vector2(MathF.Cos(rayAngle), MathF.Sin(rayAngle));

                float closestDist = float.MaxValue;
                Color wallColor = new Color(11, 18, 26, 255);

                foreach (var (rect, color) in walls)
                {
                    float? hit = RaycastRect(camPos, rayDir, rect);
                    if (hit.HasValue && hit.Value < closestDist && hit.Value > 10f)
                    {
                        closestDist = hit.Value;
                        float brightness = MathHelper.Clamp(1f - closestDist / 3000f, 0.2f, 1f);
                        wallColor = new Color(
                            (int)(color.R * brightness),
                            (int)(color.G * brightness),
                            (int)(color.B * brightness), 255);
                    }
                }

                if (closestDist < float.MaxValue - 1)
                {
                    float wallHeight = 250f;
                    float projectedHeight = (wallHeight / closestDist) * (sh / (2f * MathF.Tan(halfFov)));
                    int drawTop = (int)(sh / 2f - projectedHeight / 2f);
                    int drawBot = (int)(sh / 2f + projectedHeight / 2f);

                    fpBatch.Draw(pixelTex, new Rectangle(col, drawTop, 4, drawBot - drawTop), wallColor);
                }
            }

            fpBatch.End();
            DrawCharacters(cam);
        }

        // =====================================================================
        // РЕЖИМ 7: LAYERED DEPTH
        // 8 слоёв глубины, каждый слой рендерится отдельно с параллаксом
        // =====================================================================
        private static void RenderMode_LayeredDepth(Camera cam)
        {
            var gd = GameMain.Instance.GraphicsDevice;
            float[] depthRanges = { 500f, 1000f, 1500f, 2000f, 2500f, 3000f, 3500f, 4000f };

            for (int layer = 0; layer < 8; layer++)
            {
                gd.SetRenderTarget(depthTargets[layer]);
                gd.Clear(Color.Transparent);

                float minDist = layer == 0 ? 0f : depthRanges[layer - 1];
                float maxDist = depthRanges[layer];
                float parallax = (layer / 8f) * 50f;

                Matrix layerMatrix = Matrix.CreateTranslation(-MathF.Cos(camYaw) * parallax, -MathF.Sin(camPitch) * parallax, 0) * GetPerspectiveMatrix();

                fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, null, null, null, layerMatrix);

                foreach (var s in Structure.WallList)
                {
                    if (s.Submarine == null) continue;
                    float dist = Vector2.Distance(camPos, s.WorldPosition);
                    if (dist >= minDist && dist < maxDist)
                        s.Draw(fpBatch, false, true);
                }

                fpBatch.End();
            }

            gd.SetRenderTarget(null);
            gd.Clear(new Color(11, 18, 26, 255));

            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            for (int i = 7; i >= 0; i--)
            {
                float alpha = (i + 1) / 8f;
                fpBatch.Draw(depthTargets[i], Vector2.Zero, new Color(1, 1, 1, alpha));
            }
            fpBatch.End();

            DrawCharacters(cam);
        }

        // =====================================================================
        // РЕЖИМ 8: VOXEL CONVERT
        // Конвертируем 2D спрайты в "воксели" через множественные Z-слои
        // =====================================================================
        private static void RenderMode_VoxelConvert(Camera cam)
        {
            Matrix projMatrix = GetPerspectiveMatrix();
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, null, null, null, projMatrix);

            foreach (var s in Structure.WallList)
            {
                if (s.Submarine == null) continue;
                if (Vector2.Distance(camPos, s.WorldPosition) > 3000) continue;

                int voxelDepth = 8;
                for (int z = 0; z < voxelDepth; z++)
                {
                    float zOffset = z * 5f;
                    float brightness = 1.0f - (z / (float)voxelDepth) * 0.5f;

                    Matrix voxelMatrix = Matrix.CreateTranslation(zOffset, 0, 0) * GetPerspectiveMatrix();
                    fpBatch.End();
                    fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, null, null, null, null, voxelMatrix);

                    Color voxelColor = s.SpriteColor * brightness;
                    s.Draw(fpBatch, false, true);
                }
            }

            fpBatch.End();
            DrawCharacters(cam);
        }

        // =====================================================================
        // ВСПОМОГАТЕЛЬНЫЕ
        // =====================================================================
        private static void DrawCharacters(Camera cam)
        {
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
            foreach (var c in Character.CharacterList)
            {
                if (c.IsVisible && c != Character.Controlled)
                    c.Draw(fpBatch, cam);
            }
            fpBatch.End();
        }

        private static void DrawOverlay()
        {
            EnsureResources();
            int sw = GameMain.GraphicsWidth, sh = GameMain.GraphicsHeight;
            int cx = sw / 2, cy = sh / 2;

            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);

            // Прицел
            fpBatch.Draw(pixelTex, new Rectangle(cx - 10, cy, 20, 2), Color.White * 0.8f);
            fpBatch.Draw(pixelTex, new Rectangle(cx, cy - 10, 2, 20), Color.White * 0.8f);

            // Инфо
            fpBatch.Draw(pixelTex, new Rectangle(10, 10, 280, 120), new Color(0, 0, 0, 180));
            GUI.DrawString(fpBatch, new Vector2(20, 20), $"Mode: {MODE_NAMES[renderMode]}", Color.Lime);
            GUI.DrawString(fpBatch, new Vector2(20, 40), $"Yaw: {MathF.Round(camYaw * 57.3f)}°", Color.White);
            GUI.DrawString(fpBatch, new Vector2(20, 60), $"Pitch: {MathF.Round(camPitch * 57.3f)}°", Color.White);
            GUI.DrawString(fpBatch, new Vector2(20, 80), $"Pos: {camPos.X:0}, {camPos.Y:0}", Color.White);
            GUI.DrawString(fpBatch, new Vector2(20, 100), $"F5=Toggle | F2=Next Mode", Color.Yellow);

            fpBatch.End();
        }
    }
}