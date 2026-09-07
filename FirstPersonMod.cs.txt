using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
namespace FirstPersonMod
{
    public class FirstPersonCamera : ACsMod
    {
        private const float MOUSE_SENSITIVITY = 0.003f;
        private static bool isFirstPerson = false;
        private static float camYaw = 0f;
        private static float camPitch = 0f;
        private static readonly Dictionary<(Texture2D tex, Rectangle src), Color[]> spriteRegionPixelCache
            = new Dictionary<(Texture2D, Rectangle), Color[]>();
        private static Vector2 savedCameraPos;
        private static float savedCameraZoom;
        private static bool savedLosEnabled;
        private static bool savedLightingEnabled;
        
        private static Texture2D pixelTexture;
        private static SpriteBatch fpBatch;
        
        private static FieldInfo transformField;
        private static FieldInfo shaderTransformField;
        private static FieldInfo doorSpriteField;
        private static List<Structure> angledStructures = new List<Structure>();
        private static float layerDepth = 150f;
        private static float fov = MathF.PI / 2.5f;
        
        private static Vector2 camOffset = Vector2.Zero;
        private const float STRAFE_SPEED = 3f;

        private static float roomLeft, roomRight, roomFloor, roomCeiling;
        private static Hull currentHull = null;

        private static bool xrayMode = false;
        private static int renderMode = 0;
        private const int RENDER_MODE_COUNT = 2;

        private static int brightnessMode = 1;

        private static bool bgEnabled = true;
        private static bool cameraFollowMode = true;

        private static DepthStencilState depthWriteState;
        private static DepthStencilState depthReadState;
		
		
        private static List<Structure> horizontalStructures = new List<Structure>();
        private static List<Structure> verticalStructures = new List<Structure>();
        private static List<Structure> stairStructures = new List<Structure>();
        private static List<Item> doorItems = new List<Item>();

        private static Direction lockedDirection = Direction.Right;
        private static bool isMovingBackward = false;



        private static Rectangle GetVerticalSurfaceStrip(Rectangle src, bool left)
        {
            int stripW = Math.Clamp(src.Width / 4, 8, 32);
            if (left)
            {
                return new Rectangle(src.X, src.Y, stripW, src.Height);
            }
            return new Rectangle(src.Right - stripW, src.Y, stripW, src.Height);
        }
        
        /// <summary>
        /// Тайлинг только по X. По глубине (dest.Height) текстура растягивается одним "слоем",
        /// чтобы не было шахматного дубля по Z на полу/потолке.
        /// </summary>
        private static void DrawSurfaceStripAlongX(
            SpriteBatch sb,
            Texture2D tex,
            Rectangle srcStrip,
            Rectangle dest,
            Color tint,
            float scale,
            int phaseX = 0)
        {
            if (tex == null || tex.IsDisposed || dest.Width <= 0 || dest.Height <= 0) return;
        
            int tileW = Math.Max((int)(srcStrip.Width * scale), 1);
            int startX = dest.X - MathUtils.PositiveModulo(dest.X + phaseX, tileW);
        
            for (int x = startX; x < dest.Right; x += tileW)
            {
                int drawX = Math.Max(x, dest.X);
                int drawW = Math.Min(x + tileW, dest.Right) - drawX;
                if (drawW <= 0) continue;
        
                int srcX = srcStrip.X + (int)((drawX - x) / scale);
                int srcW = Math.Max(1, (int)MathF.Ceiling(drawW / scale));
                if (srcX + srcW > srcStrip.Right) srcW = Math.Max(1, srcStrip.Right - srcX);
        
                sb.Draw(
                    tex,
                    new Rectangle(drawX, dest.Y, drawW, dest.Height),
                    new Rectangle(srcX, srcStrip.Y, srcW, srcStrip.Height),
                    tint);
            }
        }
        
        /// <summary>
        /// Тайлинг только по Y. По глубине (dest.Width) текстура растягивается одним "слоем",
        /// чтобы торцы не выглядели как повторяющийся 2D-паттерн.
        /// </summary>
        private static void DrawSurfaceStripAlongY(
            SpriteBatch sb,
            Texture2D tex,
            Rectangle srcStrip,
            Rectangle dest,
            Color tint,
            float scale,
            int phaseY = 0)
        {
            if (tex == null || tex.IsDisposed || dest.Width <= 0 || dest.Height <= 0) return;
        
            int tileH = Math.Max((int)(srcStrip.Height * scale), 1);
            int startY = dest.Y - MathUtils.PositiveModulo(dest.Y + phaseY, tileH);
        
            for (int y = startY; y < dest.Bottom; y += tileH)
            {
                int drawY = Math.Max(y, dest.Y);
                int drawH = Math.Min(y + tileH, dest.Bottom) - drawY;
                if (drawH <= 0) continue;
        
                int srcY = srcStrip.Y + (int)((drawY - y) / scale);
                int srcH = Math.Max(1, (int)MathF.Ceiling(drawH / scale));
                if (srcY + srcH > srcStrip.Bottom) srcH = Math.Max(1, srcStrip.Bottom - srcY);
        
                sb.Draw(
                    tex,
                    new Rectangle(dest.X, drawY, dest.Width, drawH),
                    new Rectangle(srcStrip.X, srcY, srcStrip.Width, srcH),
                    tint);
            }
        }


        private sealed class ExtrudedSpriteMesh
        {
            public Texture2D Texture;
            public VertexPositionColorTexture[] SideVertices;
            public short[] SideIndices;
        }

        private static readonly Dictionary<(Texture2D tex, Rectangle src, byte alpha), ExtrudedSpriteMesh> extrudedMeshCache
            = new Dictionary<(Texture2D, Rectangle, byte), ExtrudedSpriteMesh>();



        private static bool IsAngledOrFlippedStructure(Structure st)
        {
            if (st == null) return false;
        
            // Любой поворот, либо флип => в отдельный проход
            if (MathF.Abs(st.RotationRad) > 0.0001f) return true;
            if (st.FlippedX || st.FlippedY) return true;
        
            return false;
        }
        private static MapEntity TryGetInteractableFromBody(Body body)
        {
            if (body == null) return null;

            object ud = body.UserData;
            if (ud is Item it)
            {
                if (IsInteractableCandidate(it)) return it;
            }
            else if (ud is Structure st)
            {
                if (IsInteractableCandidate(st)) return st;
            }

            return null;
        }

        private static bool blockFlip = false;
        // ==================== ДОБАВЬ ЭТИ ПЕРЕМЕННЫЕ В НАЧАЛО КЛАССА ====================
        private static int stretchMethod = 0;
        private const int STRETCH_METHOD_COUNT = 10;
        private static bool show2DWindow = false;
        private static RenderTarget2D debugRenderTarget;
        private static int debugWindowSize = 300;
        private static readonly Dictionary<Limb, float> armExtraRotDegState = new Dictionary<Limb, float>();
        private const float LOOK_UP_THRESHOLD = 0.3f;
        private const float LOOK_DOWN_THRESHOLD = -0.3f;

        private static int flipCooldown = 0;

        private static bool blockKeySetState = false;
        private static HashSet<InputType> controlledInputs = new HashSet<InputType>
        {
            InputType.Left, InputType.Right, InputType.Up, InputType.Down
        };
        
        private static FieldInfo keyInputTypeField = null;
        private static FieldInfo pullJointField =
                    typeof(Limb).GetField("pullJoint", BindingFlags.NonPublic | BindingFlags.Instance);
        
        private static readonly Dictionary<Limb, Vector2> basePullAnchors = new Dictionary<Limb, Vector2>();
        

        private static void TryInteractWithLookTarget()
        {
            var ch = Character.Controlled;
            if (ch == null) return;

            // Всегда обновим cursor в момент интеракта
            UpdateFirstPersonCursorTarget();

            if (fpLookTarget == null)
            {
                DebugConsole.NewMessage("FP Interact: no look target", Color.Orange);
                return;
            }

            if (TryGetEntityCenter(fpLookTarget, out var c))
            {
                ch.CursorPosition = c;
            }

            if (fpLookTarget is Item it)
            {
                try
                {
                    var m = typeof(Item).GetMethod("TryInteract", new Type[] { typeof(Character), typeof(bool), typeof(bool) });
                    if (m != null)
                    {
                        m.Invoke(it, new object[] { ch, true, true });
                        return;
                    }

                    m = typeof(Item).GetMethod("TryInteract", new Type[] { typeof(Character) });
                    if (m != null)
                    {
                        m.Invoke(it, new object[] { ch });
                        return;
                    }

                    m = typeof(Item).GetMethod("Interact", new Type[] { typeof(Character) });
                    if (m != null)
                    {
                        m.Invoke(it, new object[] { ch });
                        return;
                    }
                }
                catch (Exception ex)
                {
                    DebugConsole.NewMessage("FP Interact item error: " + ex.Message, Color.Red);
                }
            }

            // Fallback: дергаем Use через keys
            try
            {
                if (Enum.TryParse("Use", out InputType useType))
                {
                    var key = ch.Keys[(int)useType];
                    key.SetState(false, true);
                    key.Held = true;
                    return;
                }

                if (Enum.TryParse("Interact", out InputType interactType))
                {
                    var key = ch.Keys[(int)interactType];
                    key.SetState(false, true);
                    key.Held = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage("FP Interact fallback error: " + ex.Message, Color.Red);
            }
        }	
		
        private const float LADDER_INTERACT_DISTANCE = 10f;

        private static bool flipInProgress = false;
        private static int flipFreezeFrames = 0;

        private static int freezeMode = 0;
        private const int FREEZE_MODE_COUNT = 6;
        private static readonly int[] freezeDurations = { 0, 3, 5, 8, 12, 20 };
        
        private static int stairRenderMode = 0;
        
        private static int characterRenderMode = 0;
        private const int CHARACTER_MODE_COUNT = 11;

        // Параметры: (headThick, torsoThick, armOffset, armThick, legOffset, legThick)
        // headThick - толщина головы (растяг по Z)
        // torsoThick - толщина торса
        // armOffset - насколько руки отодвинуты от центра
        // armThick - толщина рук (растяг)
        // legOffset - насколько ноги отодвинуты от центра
        // legThick - толщина ног (растяг)
		
//         (2, 6, 28, 5, 10, 5, 0,  0,  0,  0),  
//                              │   │   │   └── ноги сдвинуты вниз на 15
//                              │   │   └────── руки сдвинуты вниз на 5
//                              │   └────────── торс без сдвига
//                              └────────────── голова сдвинута вниз на 10
        private static readonly (float, float, float, float, float, float, float, float, float, float)[] characterModeParams = 
        {
            (0, 0, 0, 0, 0, 0, 0, 0, 0, 0),                     // 0: 2D
            (2, 6, 28, 5, 10, 5, -5, 0, 0, 0),                   // 1: Minimal (база)
            (3, 8, 32, 6, 12, 6, 0, 0, 0, 0),                   // 2: Slim
            (4, 10, 36, 8, 15, 8, 0, 0, 0, 0),                  // 3: Light
            (5, 12, 40, 10, 18, 10, 0, 0, 0, 0),                // 4: Balanced
            (6, 14, 44, 12, 20, 12, 0, 0, 0, 0),                // 5: Medium
            (8, 16, 48, 14, 24, 14, 0, 0, 0, 0),                // 6: Solid
            (10, 18, 52, 16, 28, 16, 0, 0, 0, 0),               // 7: Heavy
            (12, 20, 56, 18, 32, 18, 0, 0, 0, 0),               // 8: Chunky
            (15, 24, 64, 22, 38, 22, 0, 0, 0, 0),               // 9: Thick
            (20, 30, 80, 28, 45, 28, 0, 0, 0, 0),               // 10: Maximum
        };
		
        private static void DrawHullBoundaryFallback(bool needFloor, bool needCeiling)
        {
            // roomLeft/right/floor/ceiling уже заполняются в FindCurrentRoom
            float widthF = roomRight - roomLeft;
            if (widthF <= 1f) return;
        
            int zStart = (int)(-layerDepth);
            int zLen = (int)(layerDepth * 2f);
            int width = Math.Max(1, (int)widthF);
        
            Color floorColor = new Color(70, 80, 95);
            Color ceilColor = new Color(90, 100, 115);
        
            if (needCeiling)
            {
                fpBatch.Begin(
                    SpriteSortMode.Deferred,
                    BlendState.Opaque,
                    SamplerState.LinearWrap,
                    depthWriteState,
                    RasterizerState.CullNone,
                    null,
                    BuildFloorMatrix(roomCeiling));
        
                fpBatch.Draw(pixelTexture, new Rectangle((int)roomLeft, zStart, width, zLen), ceilColor);
                fpBatch.End();
            }
        
            if (needFloor)
            {
                fpBatch.Begin(
                    SpriteSortMode.Deferred,
                    BlendState.Opaque,
                    SamplerState.LinearWrap,
                    depthWriteState,
                    RasterizerState.CullNone,
                    null,
                    BuildFloorMatrix(roomFloor));
        
                fpBatch.Draw(pixelTexture, new Rectangle((int)roomLeft, zStart, width, zLen), floorColor);
                fpBatch.End();
            }
        }
        private static BasicEffect basicEffect;
        private static VertexPositionColorTexture[] quadVertices = new VertexPositionColorTexture[4];
        private static short[] quadIndices = new short[] { 0, 1, 2, 2, 1, 3 };

        private static int rightLimbFixMode = 0;
        private const int RIGHT_LIMB_FIX_MODE_COUNT = 6;


        // ==================== НАСТРОЙКИ ЧАСТЕЙ ТЕЛА ====================
        // Offset = насколько далеко от центра по оси Z (- влево, + вправо)
        // Thickness = толщина/растяг части тела
        // YOffset = сдвиг по вертикали (+ вверх, - вниз)
        // ================================================================
        
        // ГОЛОВА
        private static float headOffset = 0f;        // позиция по глубине (0 = центр)
        private static float headThickness = 2f;     // толщина головы
        private static float headYOffset = 10f;      // сдвиг вверх/вниз
        
        // ТУЛОВИЩЕ (грудь)
        private static float torsoOffset = 0f;       // позиция по глубине (0 = центр)
        private static float torsoThickness = 6f;    // толщина туловища
        private static float torsoYOffset = 0f;      // сдвиг вверх/вниз
        
        // ТАЛИЯ (нижняя часть туловища)
        private static float waistOffset = 0f;       // позиция по глубине (0 = центр)
        private static float waistThickness = 5f;    // толщина талии
        private static float waistYOffset = 0f;      // сдвиг вверх/вниз
        
        // ЛЕВОЕ ПЛЕЧО (верхняя часть руки)
        private static float leftArmOffset = -35f;   // позиция по глубине (- = левее)
        private static float leftArmThickness = 5f;  // толщина
        private static float leftArmYOffset = 0f;    // сдвиг вверх/вниз
        
        // ПРАВОЕ ПЛЕЧО (верхняя часть руки)
        private static float rightArmOffset = 35f;   // позиция по глубине (+ = правее)
        private static float rightArmThickness = 5f; // толщина
        private static float rightArmYOffset = 0f;   // сдвиг вверх/вниз
        
        // ЛЕВОЕ ПРЕДПЛЕЧЬЕ (средняя часть руки)
        private static float leftForearmOffset = -38f;   // позиция по глубине
        private static float leftForearmThickness = 4f;  // толщина
        private static float leftForearmYOffset = 0f;    // сдвиг вверх/вниз
        
        // ПРАВОЕ ПРЕДПЛЕЧЬЕ (средняя часть руки)
        private static float rightForearmOffset = 38f;   // позиция по глубине
        private static float rightForearmThickness = 4f; // толщина
        private static float rightForearmYOffset = 0f;   // сдвиг вверх/вниз
        
        // ЛЕВАЯ КИСТЬ (рука)
        private static float leftHandOffset = -40f;  // позиция по глубине
        private static float leftHandThickness = 3f; // толщина
        private static float leftHandYOffset = 0f;   // сдвиг вверх/вниз
        
        // ПРАВАЯ КИСТЬ (рука)
        private static float rightHandOffset = 40f;  // позиция по глубине
        private static float rightHandThickness = 3f;// толщина
        private static float rightHandYOffset = 0f;  // сдвиг вверх/вниз
        
        // ЛЕВОЕ БЕДРО (верхняя часть ноги)
        private static float leftThighOffset = -8f;  // позиция по глубине
        private static float leftThighThickness = 5f;// толщина
        private static float leftThighYOffset = 0f;  // сдвиг вверх/вниз
        
        // ПРАВОЕ БЕДРО (верхняя часть ноги)
        private static float rightThighOffset = 8f;  // позиция по глубине
        private static float rightThighThickness = 5f;// толщина
        private static float rightThighYOffset = 0f; // сдвиг вверх/вниз
        
        // ЛЕВАЯ ГОЛЕНЬ (средняя часть ноги)
        private static float leftLegOffset = -10f;   // позиция по глубине
        private static float leftLegThickness = 4f;  // толщина
        private static float leftLegYOffset = 0f;    // сдвиг вверх/вниз
        
        // ПРАВАЯ ГОЛЕНЬ (средняя часть ноги)
        private static float rightLegOffset = 10f;   // позиция по глубине
        private static float rightLegThickness = 4f; // толщина
        private static float rightLegYOffset = 0f;   // сдвиг вверх/вниз
        
        // ЛЕВАЯ СТОПА (нога)
        private static float leftFootOffset = -12f;  // позиция по глубине
        private static float leftFootThickness = 3f; // толщина
        private static float leftFootYOffset = 0f;   // сдвиг вверх/вниз
        
        // ПРАВАЯ СТОПА (нога)
        private static float rightFootOffset = 12f;  // позиция по глубине
        private static float rightFootThickness = 3f;// толщина
        private static float rightFootYOffset = 0f;  // сдвиг вверх/вниз



        // ==================== РЕЖИМ ОТОБРАЖЕНИЯ ЧАСТЕЙ ТЕЛА ====================
        // 0 = все части, 1 = только голова, 2 = только туловище, 
        // 3 = только левая рука, 4 = только правая рука,
        // 5 = только левая нога, 6 = только правая нога
        // =======================================================================
                // ==================== РЕЖИМ ОТОБРАЖЕНИЯ ЧАСТЕЙ ТЕЛА ====================
        private static int limbDisplayMode = 0;
        private const int LIMB_DISPLAY_MODE_COUNT = 7;

        // Выбранная подчасть для редактирования (внутри группы)
        private static int selectedSubPart = 0;

        // Количество подчастей для каждого режима
        private static int GetSubPartCount(int mode) => mode switch
        {
            0 => 0,  // ALL — нет подчастей
            1 => 1,  // HEAD — 1 часть (голова)
            2 => 2,  // TORSO — 2 части (торс, талия)
            3 => 3,  // LEFT ARM — 3 части (плечо, предплечье, кисть)
            4 => 3,  // RIGHT ARM — 3 части
            5 => 3,  // LEFT LEG — 3 части (бедро, голень, стопа)
            6 => 3,  // RIGHT LEG — 3 части
            _ => 0
        };
		
       // Добавь эту переменную в начало класса (рядом с другими настройками)
        private static float texturePadding = 0.05f; // 5% отступ от краёв
		
        private static int charRenderFixMode = 0; // 0..5
        private const int CHAR_RENDER_FIX_MODE_COUNT = 3;
		
        private static string GetLimbDisplayModeName(int mode) => mode switch
        {
            0 => "ALL (все части)",
            1 => "HEAD (только голова)",
            2 => "TORSO (только туловище)",
            3 => "LEFT ARM (только левая рука)",
            4 => "RIGHT ARM (только правая рука)",
            5 => "LEFT LEG (только левая нога)",
            6 => "RIGHT LEG (только правая нога)",
            _ => "Unknown"
        };


        private static string GetSubPartName(int mode, int subPart) => mode switch
        {
            1 => "Head (голова)",
            2 => subPart switch
            {
                0 => "Torso (грудь)",
                1 => "Waist (талия)",
                _ => "Unknown"
            },
            3 => subPart switch
            {
                0 => "LeftArm (плечо)",
                1 => "LeftForearm (предплечье)",
                2 => "LeftHand (кисть)",
                _ => "Unknown"
            },
            4 => subPart switch
            {
                0 => "RightArm (плечо)",
                1 => "RightForearm (предплечье)",
                2 => "RightHand (кисть)",
                _ => "Unknown"
            },
            5 => subPart switch
            {
                0 => "LeftThigh (бедро)",
                1 => "LeftLeg (голень)",
                2 => "LeftFoot (стопа)",
                _ => "Unknown"
            },
            6 => subPart switch
            {
                0 => "RightThigh (бедро)",
                1 => "RightLeg (голень)",
                2 => "RightFoot (стопа)",
                _ => "Unknown"
            },
            _ => "None"
        };
        // ==================== ДОБАВЬ ПЕРЕМЕННЫЕ В НАЧАЛО КЛАССА ====================
        private static int spriteDebugMode = 0;
        private const int SPRITE_DEBUG_MODE_COUNT = 8;
        // Получить/установить offset для выбранной подчасти
        private static float GetSelectedOffset()
        {
            return (limbDisplayMode, selectedSubPart) switch
            {
                (1, 0) => headOffset,
                (2, 0) => torsoOffset,
                (2, 1) => waistOffset,
                (3, 0) => leftArmOffset,
                (3, 1) => leftForearmOffset,
                (3, 2) => leftHandOffset,
                (4, 0) => rightArmOffset,
                (4, 1) => rightForearmOffset,
                (4, 2) => rightHandOffset,
                (5, 0) => leftThighOffset,
                (5, 1) => leftLegOffset,
                (5, 2) => leftFootOffset,
                (6, 0) => rightThighOffset,
                (6, 1) => rightLegOffset,
                (6, 2) => rightFootOffset,
                _ => 0f
            };
        }
        private static float NormalizeDeg180(float deg)
        {
            float d = deg % 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }
        private static string GetStructureIdLowerSafe(Structure st)
        {
            try { return st?.Prefab?.Identifier.ToString().ToLowerInvariant() ?? ""; }
            catch { return ""; }
        }
		
        private static bool IsFrontById(Structure st)
        {
            string id = GetStructureIdLower(st);
            return id.Contains("front");
        }

        private static bool IsTailById(Structure st)
        {
            string id = GetStructureIdLowerSafe(st);
            return id.Contains("tail") || id.Contains("fin");
        }

        private static bool IsShellById(Structure st)
        {
            string id = GetStructureIdLower(st);
            return id.Contains("shell") || id.Contains("shuttle") || id.Contains("front") || id.Contains("tail") || id.Contains("bumper");
        }

        private static string GetStructureIdLower(Structure st)
        {
            try { return st?.Prefab?.Identifier.ToString().ToLowerInvariant() ?? ""; }
            catch { return ""; }
        }

        private static bool ResolveSpecialShellMirrorXByFixMode(Structure st, int mirrorMode, int fixMode)
        {
            bool baseMirror = ResolveMirrorX(st, mirrorMode);

            bool isLower = IsLowerShellPart(st);
            bool isFront = IsFrontById(st);
            bool isTail = IsTailById(st);

            float br = NormalizeDeg180(GetSpecialShellBodyRotDeg(st));
            bool signMirror = br < 0f;
            bool steep = MathF.Abs(br) > 45f;

            return fixMode switch
            {
                0 => baseMirror,
                1 => isTail ? !baseMirror : baseMirror,
                2 => isLower ? !baseMirror : baseMirror,
                3 => baseMirror ^ isTail ^ isLower, // твой сейчас самый полезный тестовый
                4 => isTail ? !baseMirror : baseMirror,
                5 => signMirror,
                6 => signMirror ^ isLower,
                7 => !baseMirror,
                8 => baseMirror ^ isTail ^ steep,
                9 => baseMirror ^ isTail ^ (signMirror && isLower),
                10 => (isFront ? baseMirror : !baseMirror) ^ (steep && isLower),
                11 => (isFront ? baseMirror : !baseMirror) ^ isLower,
                _ => baseMirror
            };
        }


        // ==================== АВТОМАТИЧЕСКИЙ РАСЧЁТ ПАРАМЕТРОВ ====================
        

       
        private static (float zOffset, float thickness) CalculateLimbParamsAuto(Limb limb)
        {
            Sprite sprite = GetLimbSprite(limb);
            if (sprite == null) return (0f, 10f);
            
            float depth = sprite.Depth;
            float centerDepth = 0.25f;
            float depthRange = 0.5f;
            
            float normalizedDepth = (depth - centerDepth) / depthRange;
            normalizedDepth = MathHelper.Clamp(normalizedDepth, -1f, 1f);
            
            float zOffset = -normalizedDepth * 30f;
            
            float scale = limb.Scale * limb.TextureScale;
            float spriteWidth = sprite.SourceRect.Width * scale;
            float spriteHeight = sprite.SourceRect.Height * scale;
            
            float avgSize = (spriteWidth + spriteHeight) / 2f;
            float thickness = avgSize * 0.5f;
            thickness = Math.Max(thickness, 10f);
            
            return (zOffset, thickness);
        }
         
        private static AlphaTestEffect alphaTestEffect;
        // ==================== СМЕЩЕНИЯ ГРАНЕЙ ДЛЯ КАЖДОЙ ЧАСТИ ====================
        // Каждая часть имеет 6 граней: front, back, top, bottom, left, right
        // Значения = дополнительное смещение грани от базовой позиции
        // ==========================================================================

        // ГОЛОВА - смещения граней
        private static float headFrontOffset = 0f;    // передняя грань (+ = дальше от центра)
        private static float headBackOffset = 0f;     // задняя грань (+ = дальше от центра)
        private static float headTopOffset = 0f;      // верхняя грань (+ = выше)
        private static float headBottomOffset = 0f;   // нижняя грань (+ = ниже)
        private static float headLeftOffset = 0f;     // левая грань (+ = левее)
        private static float headRightOffset = 0f;    // правая грань (+ = правее)

        // ТУЛОВИЩЕ - смещения граней
        private static float torsoFrontOffset = 0f;
        private static float torsoBackOffset = 0f;
        private static float torsoTopOffset = 0f;
        private static float torsoBottomOffset = 0f;
        private static float torsoLeftOffset = 0f;
        private static float torsoRightOffset = 0f;

        // ТАЛИЯ - смещения граней
        private static float waistFrontOffset = 0f;
        private static float waistBackOffset = 0f;
        private static float waistTopOffset = 0f;
        private static float waistBottomOffset = 0f;
        private static float waistLeftOffset = 0f;
        private static float waistRightOffset = 0f;

        // ЛЕВОЕ ПЛЕЧО - смещения граней
        private static float leftArmFrontOffset = 0f;
        private static float leftArmBackOffset = 0f;
        private static float leftArmTopOffset = 0f;
        private static float leftArmBottomOffset = 0f;
        private static float leftArmLeftOffset = 0f;
        private static float leftArmRightOffset = 0f;

        // ПРАВОЕ ПЛЕЧО - смещения граней
        private static float rightArmFrontOffset = 0f;
        private static float rightArmBackOffset = 0f;
        private static float rightArmTopOffset = 0f;
        private static float rightArmBottomOffset = 0f;
        private static float rightArmLeftOffset = 0f;
        private static float rightArmRightOffset = 0f;

        // ЛЕВОЕ ПРЕДПЛЕЧЬЕ - смещения граней
        private static float leftForearmFrontOffset = 0f;
        private static float leftForearmBackOffset = 0f;
        private static float leftForearmTopOffset = 0f;
        private static float leftForearmBottomOffset = 0f;
        private static float leftForearmLeftOffset = 0f;
        private static float leftForearmRightOffset = 0f;

        // ПРАВОЕ ПРЕДПЛЕЧЬЕ - смещения граней
        private static float rightForearmFrontOffset = 0f;
        private static float rightForearmBackOffset = 0f;
        private static float rightForearmTopOffset = 0f;
        private static float rightForearmBottomOffset = 0f;
        private static float rightForearmLeftOffset = 0f;
        private static float rightForearmRightOffset = 0f;

        // ЛЕВАЯ КИСТЬ - смещения граней
        private static float leftHandFrontOffset = 0f;
        private static float leftHandBackOffset = 0f;
        private static float leftHandTopOffset = 0f;
        private static float leftHandBottomOffset = 0f;
        private static float leftHandLeftOffset = 0f;
        private static float leftHandRightOffset = 0f;

        // ПРАВАЯ КИСТЬ - смещения граней
        private static float rightHandFrontOffset = 0f;
        private static float rightHandBackOffset = 0f;
        private static float rightHandTopOffset = 0f;
        private static float rightHandBottomOffset = 0f;
        private static float rightHandLeftOffset = 0f;
        private static float rightHandRightOffset = 0f;

        // ЛЕВОЕ БЕДРО - смещения граней
        private static float leftThighFrontOffset = 0f;
        private static float leftThighBackOffset = 0f;
        private static float leftThighTopOffset = 0f;
        private static float leftThighBottomOffset = 0f;
        private static float leftThighLeftOffset = 0f;
        private static float leftThighRightOffset = 0f;

        // ПРАВОЕ БЕДРО - смещения граней
        private static float rightThighFrontOffset = 0f;
        private static float rightThighBackOffset = 0f;
        private static float rightThighTopOffset = 0f;
        private static float rightThighBottomOffset = 0f;
        private static float rightThighLeftOffset = 0f;
        private static float rightThighRightOffset = 0f;

        // ЛЕВАЯ ГОЛЕНЬ - смещения граней
        private static float leftLegFrontOffset = 0f;
        private static float leftLegBackOffset = 0f;
        private static float leftLegTopOffset = 0f;
        private static float leftLegBottomOffset = 0f;
        private static float leftLegLeftOffset = 0f;
        private static float leftLegRightOffset = 0f;

        // ПРАВАЯ ГОЛЕНЬ - смещения граней
        private static float rightLegFrontOffset = 0f;
        private static float rightLegBackOffset = 0f;
        private static float rightLegTopOffset = 0f;
        private static float rightLegBottomOffset = 0f;
        private static float rightLegLeftOffset = 0f;
        private static float rightLegRightOffset = 0f;

        // ЛЕВАЯ СТОПА - смещения граней
        private static float leftFootFrontOffset = 0f;
        private static float leftFootBackOffset = 0f;
        private static float leftFootTopOffset = 0f;
        private static float leftFootBottomOffset = 0f;
        private static float leftFootLeftOffset = 0f;
        private static float leftFootRightOffset = 0f;

        // ПРАВАЯ СТОПА - смещения граней
        private static float rightFootFrontOffset = 0f;
        private static float rightFootBackOffset = 0f;
        private static float rightFootTopOffset = 0f;
        private static float rightFootBottomOffset = 0f;
        private static float rightFootLeftOffset = 0f;
        private static float rightFootRightOffset = 0f;

        // ==================== ВЫБОР ГРАНИ ====================
        private static int selectedFace = 0;  // 0=front, 1=back, 2=top, 3=bottom, 4=left, 5=right
        private const int FACE_COUNT = 6;

        private static int floorCeilingTestMode = 0;
        private const int FLOOR_CEILING_TEST_MODE_COUNT = 5;

        private static string GetFloorCeilingTestModeName(int mode) => mode switch
        {
            0 => "Original tiled (текущий)",
            1 => "Surface strip tiled",
            2 => "Surface strip stretched",
            3 => "Full texture stretched",
            4 => "Solid color",
            _ => "Unknown"
        };
        private static bool showLimbDebug = false;
        private static int handFixMode = 0;
        private const int HAND_FIX_MODE_COUNT = 16;
		
        private static bool prevLeftMouseDown = false;
		
		
        private static bool IsLeftMouseClicked()
        {
            var ms = Mouse.GetState();
            bool down = ms.LeftButton == ButtonState.Pressed;
            bool clicked = down && !prevLeftMouseDown;
            prevLeftMouseDown = down;
            return clicked;
        }
		
		
		
		
        private static void DrawStructureDamageOverlay2D(Structure st)
        {
            if (st == null || st.Removed || st.Sections == null || st.Sections.Length == 0) return;
            if (!st.HasDamage) return;

            float maxHealth = Math.Max(st.MaxHealth, 0.001f);

            foreach (var sec in st.Sections)
            {
                try
                {
                    if (sec == null) continue;
                    if (sec.damage <= 0f && sec.gap == null) continue;

                    var wr = sec.WorldRect;
                    if (wr.Width <= 0 || wr.Height <= 0) continue;

                    float dmg01 = MathHelper.Clamp(sec.damage / maxHealth, 0f, 1f);

                    // Полупрозрачная заливка по степени урона
                    Color dmgColor = Color.Lerp(
                        new Color(255, 200, 80, 30),
                        new Color(255, 40, 40, 140),
                        dmg01);

                    // Преобразование Y как в debug-рендере
                    fpBatch.Draw(
                        pixelTexture,
                        new Rectangle(wr.X, -wr.Y, wr.Width, wr.Height),
                        dmgColor);

                    // Если есть gap — дополнительная подсветка
                    if (sec.gap != null)
                    {
                        var gr = sec.gap.WorldRect;
                        Color gapColor = new Color(80, 220, 255, 170);

                        fpBatch.Draw(
                            pixelTexture,
                            new Rectangle(gr.X, -gr.Y, gr.Width, gr.Height),
                            gapColor);
                    }
                }
                catch { }
            }
        }
		
        private static Structure debugShellTarget = null;
        private static ushort debugShellTargetId = Entity.NullEntityID;
        private static bool debugShellTargetMirrorInvert = false;
		
        private static bool IsDebugShellTarget(Structure st)
        {
            if (st == null) return false;
            if (debugShellTargetId != Entity.NullEntityID && st.ID == debugShellTargetId) return true;
            return ReferenceEquals(st, debugShellTarget);
        }
		
		
		
		
        private static bool IsNonOrthogonalAngleDeg(float deg)
        {
            float a = MathF.Abs(deg) % 360f;
            float nearest = MathF.Round(a / 90f) * 90f;
            return MathF.Abs(a - nearest) > 1.5f;
        }
        private static bool IsArmChainPart(LimbType t)
        {
            return t == LimbType.LeftArm ||
                   t == LimbType.LeftForearm ||
                   t == LimbType.LeftHand ||
                   t == LimbType.RightArm ||
                   t == LimbType.RightForearm ||
                   t == LimbType.RightHand;
        }

        private static void DrawDamagedStructures3D(Vector2 camPos)
        {
            fpBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.NonPremultiplied,
                null,
                DepthStencilState.None,
                RasterizerState.CullNone,
                null,
                BuildWallMatrix(0f));

            foreach (var st in Structure.WallList)
            {
                try
                {
                    if (st == null || st.Removed || st.Submarine == null) continue;
                    if (st.Sections == null || st.Sections.Length == 0) continue;
                    if (Vector2.Distance(camPos, st.WorldPosition) > 3500f) continue;

                    float maxHealth = Math.Max(st.MaxHealth, 0.001f);

                    for (int i = 0; i < st.Sections.Length; i++)
                    {
                        var sec = st.Sections[i];
                        if (sec == null) continue;

                        bool hasGap = sec.gap != null;
                        float dmg01 = MathHelper.Clamp(sec.damage / maxHealth, 0f, 1f);
                        if (dmg01 <= 0f && !hasGap) continue;

                        var sr = sec.WorldRect;
                        if (sr.Width <= 0 || sr.Height <= 0) continue;

                        // Секция повреждена: оранжево-красный overlay
                        Color c = Color.Lerp(
                            new Color(255, 190, 80, 35),
                            new Color(255, 30, 30, 170),
                            dmg01);

                        fpBatch.Draw(
                            pixelTexture,
                            new Rectangle(sr.X, -sr.Y, sr.Width, sr.Height),
                            c);

                        // Gap (пробоина): циан overlay
                        if (hasGap)
                        {
                            var gr = sec.gap.WorldRect;
                            fpBatch.Draw(
                                pixelTexture,
                                new Rectangle(gr.X, -gr.Y, gr.Width, gr.Height),
                                new Color(70, 220, 255, 190));
                        }
                    }
                }
                catch { }
            }

            fpBatch.End();
        }


        private static bool IsSwimmingFP(Character ch)
        {
            if (ch == null) return false;
            try
            {
                if (ch.InWater) return true;
                if (ch.AnimController?.InWater == true) return true;
            }
            catch { }
            return false;
        }






        private static LimbType? GetParentArmLimbType(LimbType t)
        {
            return t switch
            {
                LimbType.LeftForearm => LimbType.LeftArm,
                LimbType.LeftHand => LimbType.LeftForearm,
                LimbType.RightForearm => LimbType.RightArm,
                LimbType.RightHand => LimbType.RightForearm,
                _ => null
            };
        }
        
        private static Limb FindLimbByType(Character character, LimbType type)
        {
            var limbs = character?.AnimController?.Limbs;
            if (limbs == null) return null;
            for (int i = 0; i < limbs.Length; i++)
            {
                if (limbs[i]?.type == type) return limbs[i];
            }
            return null;
        }
        
        private static Vector2 RotateLocal(float x, float y, float rot)
        {
            float c = MathF.Cos(rot);
            float s = MathF.Sin(rot);
            return new Vector2(x * c - y * s, x * s + y * c);
        }
		
        private static bool ShouldInvertMirrorForObservedShellCases(
            Structure st,
            bool inferredFront,
            bool rearByPos,
            bool lowerByWorld)
        {
            string id = "";
            try { id = st?.Prefab?.Identifier.ToString().ToLowerInvariant() ?? ""; } catch { }

            bool flipX = st?.FlippedX ?? false;
            bool flipY = st?.FlippedY ?? false;

            float bodyDeg = 0f;
            try { bodyDeg = GetSpecialShellBodyRotDeg(st); } catch { }

            // 1) Нижние "неправильные" куски у тебя стабильно: lower=false + flipY=true => нужно инвертить
            if (!lowerByWorld && flipY) return true;

            // 2) shella42deg у тебя оба куска требовали инверсии (flipX и без)
            if (id.Contains("shella42deg")) return true;

            // 3) Для rear-корпуса с отрицательным bodyRot и flipY часто нужно инвертить
            if (rearByPos && bodyDeg < -10f && flipY) return true;

            // 4) Передний combo низ (shellacombo12) ты явно подтвердил как требующий инверсии
            if (id.Contains("shellacombo12") && !lowerByWorld) return true;

            // 5) Нижняя длинная передняя shella0deg (flipY=true) — также нужно инвертить
            if (id.Contains("shella0deg") && flipY && !lowerByWorld) return true;

            // 6) Специфичные подтвержденные задние нижние
            if ((id.Contains("shella14degb") || id.Contains("shella17deg")) && flipY && !lowerByWorld) return true;

            return false;
        }
		
        private static void DrawFloorCeilingCap(
            Structure st,
            float yPlane,
            float stLeft,
            int drawWidth,
            int zStart,
            int zLen,
            Color tint,
            bool useBottomStrip)
        {
            if (drawWidth <= 0) return;

            if (!GetTexture(st, out var tex, out var src, out float sc))
            {
                fpBatch.Draw(
                    pixelTexture,
                    new Rectangle((int)stLeft, zStart, drawWidth, zLen),
                    tint);
                return;
            }

            DrawWallSurface(
                fpBatch,
                tex,
                src,
                new Rectangle((int)stLeft, zStart, drawWidth, zLen),
                tint,
                sc,
                useBottomStrip,
                phaseX: (int)stLeft,
                phaseY: zStart);
        }




	

        private const int WALL_SURFACE_MODE_COUNT = 5;

        private static string GetWallSurfaceModeName(int mode) => mode switch
        {
            0 => "Original tiled",
            1 => "Full texture stretched",
            2 => "Center band stretched",
            3 => "Surface strip stretched",
            4 => "Surface strip tiled",
            _ => "Unknown"
        };
        private static Rectangle GetCenterBand(Rectangle src)
        {
            int bandH = Math.Clamp(src.Height / 3, 8, Math.Max(8, src.Height));
            int y = src.Y + (src.Height - bandH) / 2;
            return new Rectangle(src.X, y, src.Width, bandH);
        }
		
		
        // Manual transform for special shell pieces
        private static bool shellManualOverride = false;
        private static bool shellManualMirrorX = true;
        private static bool shellManualMirrorY = false;
        private static float shellManualExtraRotDeg = 0f;
		
        private static bool IsLowerShellPart(Structure st)
        {
            try
            {
                float partY = st.WorldRect.Y - st.WorldRect.Height * 0.5f;
                float roomMidY = (roomFloor + roomCeiling) * 0.5f;
                return partY < roomMidY;
            }
            catch
            {
                return false;
            }
        }
		
        private static int floorBottomMode = 4;
        private const int FLOOR_BOTTOM_MODE_COUNT = 6;











        private static string GetFloorBottomModeName(int mode) => mode switch
        {
            0 => "Bottom: Full tiled (normal)",
            1 => "Bottom: Full tiled (FlipY)",
            2 => "Bottom: Surface strip bottom (tiled X)",
            3 => "Bottom: Surface strip top (tiled X)",
            4 => "Bottom: Full texture stretched",
            5 => "Bottom: Solid color",
            _ => "Unknown"
        };

        private static bool IsLowerShellPartStable(Structure st)
        {
            if (st?.Submarine == null) return false;
        
            float minY = float.MaxValue;
            float maxY = float.MinValue;
        
            foreach (var s in Structure.WallList)
            {
                if (s == null || s.Removed || s.Submarine != st.Submarine) continue;
                if (!IsShellLikeById(s)) continue;
        
                var wr = s.WorldRect;
                float top = wr.Y;
                float bottom = wr.Y - wr.Height;
        
                if (bottom < minY) minY = bottom;
                if (top > maxY) maxY = top;
            }
        
            if (minY >= maxY) return false;
        
            float midY = (minY + maxY) * 0.5f;
            float cy = st.WorldRect.Y - st.WorldRect.Height * 0.5f;
        
            // Подбери знак 1 раз по факту (если будет наоборот — просто поменяй < на >)
            return cy < midY;
        }


        private const float DEBUG_SHELL_PICK_DISTANCE = 5000f;
		
        private static Structure FindNearestShellLikeStructure(Vector2 fromPos)
        {
            Structure nearest = null;
            float bestDistSq = float.MaxValue;

            foreach (var st in Structure.WallList)
            {
                try
                {
                    if (st == null || st.Removed || st.Submarine == null) continue;

                    // Берем только "горки/наружные shell-части"
                    if (!(IsSpecialAngledShell(st) || IsAngledStructure(st) || IsShellLikeById(st))) continue;

                    // Лучше ограничить текущей подлодкой игрока
                    if (Character.Controlled?.Submarine != null && st.Submarine != Character.Controlled.Submarine) continue;

                    var wr = st.WorldRect;
                    Vector2 c = new Vector2(wr.X + wr.Width * 0.5f, wr.Y - wr.Height * 0.5f);
                    float d2 = Vector2.DistanceSquared(fromPos, c);
                    if (d2 < bestDistSq)
                    {
                        bestDistSq = d2;
                        nearest = st;
                    }
                }
                catch { }
            }

            if (nearest != null && bestDistSq > DEBUG_SHELL_PICK_DISTANCE * DEBUG_SHELL_PICK_DISTANCE)
            {
                return null;
            }

            return nearest;
        }
        private static MapEntity fpLookTarget = null;
        private static float fpLookMaxDist = 2500f;


        private static bool TryGetEntityCenter(MapEntity e, out Vector2 center)
        {
            center = Vector2.Zero;
            if (e == null) return false;

            try
            {
                var wr = e.WorldRect;
                center = new Vector2(wr.X + wr.Width * 0.5f, wr.Y - wr.Height * 0.5f);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInteractableCandidate(MapEntity e)
        {
            if (e == null || e.Removed) return false;
        
            if (e is Item it)
            {
                if (it.Submarine == null) return false;
                if (it.ParentInventory != null) return false;
                return true;
            }
        
            if (e is Structure st)
            {
                if (st.Submarine == null) return false;
                if (IsBlockingStructureForLookRay(st)) return false;
                return true;
            }
        
            return false;
        }



        private static MapEntity FindBestLookTarget(Vector2 rayStart, Vector2 rayDir, float maxDist)
        {
            MapEntity best = null;
            float bestScore = float.MaxValue;

            // 2 прохода: узкий конус -> широкий конус
            // чтобы сначала цеплялось ровно по центру взгляда
            float[] dotLimits = { 0.95f, 0.80f };

            for (int pass = 0; pass < dotLimits.Length; pass++)
            {
                float dotMin = dotLimits[pass];

                foreach (var e in MapEntity.MapEntityList)
                {
                    try
                    {
                        if (!IsInteractableCandidate(e)) continue;
                        if (!TryGetEntityCenter(e, out var c)) continue;

                        Vector2 to = c - rayStart;
                        float dist = to.Length();
                        if (dist < 1f || dist > maxDist) continue;

                        Vector2 dirTo = to / dist;
                        float dot = Vector2.Dot(rayDir, dirTo);
                        if (dot < dotMin) continue; // не в направлении взгляда

                        // Чем ближе к центру (dot к 1) и ближе по дистанции — тем выше приоритет
                        float angularPenalty = (1f - dot) * 1000f;
                        float distPenalty = dist * 0.02f;
                        float score = angularPenalty + distPenalty;

                        // Двери чуть приоритетнее
                        if (e is Item item)
                        {
                            try
                            {
                                if (item.GetComponent<Door>() != null) score -= 80f;
                            }
                            catch { }
                        }

                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = e;
                        }
                    }
                    catch { }
                }

                if (best != null) return best;
            }

            return null;
        }	
		
		
		
        private static void AddInteractionInputTypesToBlockSet()
        {
            try
            {
                if (Enum.TryParse("Use", out InputType useType))
                {
                    controlledInputs.Add(useType);
                }
            }
            catch { }

            try
            {
                if (Enum.TryParse("Select", out InputType selectType))
                {
                    controlledInputs.Add(selectType);
                }
            }
            catch { }

            try
            {
                if (Enum.TryParse("Interact", out InputType interactType))
                {
                    controlledInputs.Add(interactType);
                }
            }
            catch { }
        }

        private static bool TryWorldToScreen(Vector3 worldPos, out Vector2 screenPos)
        {
            screenPos = Vector2.Zero;

            GetCameraVectors(out Vector3 cameraPos, out Vector3 forward, out Vector3 up);

            Matrix view = Matrix.CreateLookAt(cameraPos, cameraPos + forward * 100f, up);
            Matrix proj = Matrix.CreatePerspectiveFieldOfView(
                fov,
                (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight,
                1f,
                50000f);

            Vector4 clip = Vector4.Transform(new Vector4(worldPos, 1f), view * proj);

            if (MathF.Abs(clip.W) < 0.00001f) return false;
            if (clip.W <= 0f) return false; // за камерой

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            float ndcZ = clip.Z / clip.W;

            if (ndcZ < 0f || ndcZ > 1f) return false;

            float sx = (ndcX * 0.5f + 0.5f) * GameMain.GraphicsWidth;
            float sy = (-ndcY * 0.5f + 0.5f) * GameMain.GraphicsHeight;

            screenPos = new Vector2(sx, sy);
            return true;
        }
		
		
        private static bool TryRaycastLookTarget(
            Vector2 rayStart,
            Vector2 rayDir,
            float maxDist,
            out MapEntity hitEntity,
            out Vector2 hitPointWorld)
        {
            hitEntity = null;
            hitPointWorld = rayStart + rayDir * maxDist;
        
            if (rayDir.LengthSquared() < 0.000001f) return false;
            rayDir.Normalize();
        
            if (GameMain.World == null) return false;
        
            Vector2 fromSim = ConvertUnits.ToSimUnits(rayStart);
            Vector2 toSim = ConvertUnits.ToSimUnits(rayStart + rayDir * maxDist);
        
            float bestFraction = float.MaxValue;
            Body bestBody = null;
            Vector2 bestPointSim = toSim;
        
            GameMain.World.RayCast((fixture, point, normal, fraction) =>
            {
                try
                {
                    if (fixture == null || fixture.Body == null) return -1f;
                    if (fixture.IsSensor) return -1f;
        
                    var candidate = TryGetInteractableFromBody(fixture.Body);
                    if (!IsInteractableCandidate(candidate)) return -1f;
        
                    // Не даем лучу "прилипать" к корпусу/стенам/полу/потолку
                    if (candidate is Structure stCand)
                    {
                        if (IsSolidWall(stCand)) return -1f;
                        if (IsSolidFloorCeiling(stCand)) return -1f;
                        if (IsShellLikeById(stCand)) return -1f;
                    }
        
                    // Ограничение текущей подлодкой игрока (чтобы не цеплять чужие объекты)
                    var ch = Character.Controlled;
                    if (ch?.Submarine != null)
                    {
                        Submarine s = null;
                        if (candidate is Item it) s = it.Submarine;
                        else if (candidate is Structure st) s = st.Submarine;
        
                        if (s != null && s != ch.Submarine) return -1f;
                    }
        
                    if (fraction < bestFraction)
                    {
                        bestFraction = fraction;
                        bestBody = fixture.Body;
                        bestPointSim = point;
                    }
        
                    // Продолжаем, ищем самый ближний хит
                    return 1f;
                }
                catch
                {
                    return -1f;
                }
            }, fromSim, toSim);
        
            if (bestBody == null) return false;
        
            hitEntity = TryGetInteractableFromBody(bestBody);
            if (hitEntity == null) return false;
        
            hitPointWorld = ConvertUnits.ToDisplayUnits(bestPointSim);
            return true;
        }





        private static Vector2 GetFirstPersonCursorWorldPos(float maxDistance = 2500f)
        {
            Vector3 origin3, dir3;
            if (!TryGetCenterScreenRay3D(out origin3, out dir3))
            {
                GetCameraVectors(out var cp, out var fwd, out _);
                origin3 = cp;
                dir3 = Vector3.Normalize(fwd);
            }
        
            // Старт луча = голова/камера игрока
            Vector2 rayStart = GetHeadPosition();
        
            // Направление для физики (2D мир) берем из того же 3D луча
            Vector2 rayDir = new Vector2(dir3.X, dir3.Y);
            if (rayDir.LengthSquared() < 0.000001f)
            {
                rayDir = (lockedDirection == Direction.Right) ? Vector2.UnitX : -Vector2.UnitX;
            }
            else
            {
                rayDir.Normalize();
            }
        
            if (TryRaycastLookTarget(rayStart, rayDir, maxDistance, out var hitEntity, out var hitPoint))
            {
                fpLookTarget = hitEntity;
                if (TryGetEntityCenter(hitEntity, out var center))
                {
                    return center;
                }
                return hitPoint;
            }
        
            fpLookTarget = null;
            return rayStart + rayDir * maxDistance;
        }
		
        private static bool TryGetCenterScreenRay3D(out Vector3 rayOrigin, out Vector3 rayDir)
        {
            rayOrigin = Vector3.Zero;
            rayDir = Vector3.Forward;
        
            try
            {
                GetCameraVectors(out Vector3 cameraPos, out Vector3 forward, out Vector3 up);
        
                Matrix view = Matrix.CreateLookAt(cameraPos, cameraPos + forward * 100f, up);
                Matrix projection = Matrix.CreatePerspectiveFieldOfView(
                    fov,
                    (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight,
                    1f,
                    50000f);
        
                var vp = GameMain.Instance.GraphicsDevice.Viewport;
        
                Vector3 nearP = vp.Unproject(
                    new Vector3(vp.Width * 0.5f, vp.Height * 0.5f, 0f),
                    projection,
                    view,
                    Matrix.Identity);
        
                Vector3 farP = vp.Unproject(
                    new Vector3(vp.Width * 0.5f, vp.Height * 0.5f, 1f),
                    projection,
                    view,
                    Matrix.Identity);
        
                Vector3 d = farP - nearP;
                if (d.LengthSquared() < 0.000001f) return false;
                d.Normalize();
        
                rayOrigin = nearP;
                rayDir = d;
                return true;
            }
            catch
            {
                return false;
            }
        }
		
		
        private static bool TryProjectWorldToScreen(Vector3 worldPos, out Vector2 screenPos, out float depth01)
        {
            screenPos = Vector2.Zero;
            depth01 = 1f;

            try
            {
                GetCameraVectors(out Vector3 cameraPos, out Vector3 forward, out Vector3 up);

                Matrix view = Matrix.CreateLookAt(cameraPos, cameraPos + forward * 100f, up);
                Matrix proj = Matrix.CreatePerspectiveFieldOfView(
                    fov,
                    (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight,
                    1f,
                    50000f);

                var vp = GameMain.Instance.GraphicsDevice.Viewport;
                Vector3 s = vp.Project(worldPos, proj, view, Matrix.Identity);

                // depth outside clip
                if (s.Z < 0f || s.Z > 1f) return false;

                screenPos = new Vector2(s.X, s.Y);
                depth01 = s.Z;
                return true;
            }
            catch
            {
                return false;
            }
        }
        private static MapEntity FindBestLookTargetFromScreenCenter(out Vector2 bestCenterWorld)
        {
            bestCenterWorld = Vector2.Zero;

            float cx = GameMain.GraphicsWidth * 0.5f;
            float cy = GameMain.GraphicsHeight * 0.5f;

            const float maxScreenRadius = 140f; // подстрой 100..180
            const float maxScreenRadiusSq = maxScreenRadius * maxScreenRadius;

            MapEntity best = null;
            float bestScore = float.MaxValue;

            foreach (var e in MapEntity.MapEntityList)
            {
                try
                {
                    if (!IsInteractableCandidate(e)) continue;
                    if (!TryGetEntityCenter(e, out var c)) continue;

                    // Проецируем в экран. Z=0, потому что физический мир 2D.
                    if (!TryProjectWorldToScreen(new Vector3(c.X, c.Y, 0f), out var sp, out float depth01)) continue;

                    float dx = sp.X - cx;
                    float dy = sp.Y - cy;
                    float r2 = dx * dx + dy * dy;
                    if (r2 > maxScreenRadiusSq) continue; // вне прицела

                    // score: центр экрана + чуть-чуть глубина
                    float screenPenalty = MathF.Sqrt(r2);
                    float depthPenalty = depth01 * 60f; // меньше = ближе к камере
                    float score = screenPenalty + depthPenalty;

                    // двери чуть приоритетнее
                    if (e is Item it)
                    {
                        try
                        {
                            if (it.GetComponent<Door>() != null) score -= 20f;
                        }
                        catch { }
                    }

                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = e;
                        bestCenterWorld = c;
                    }
                }
                catch { }
            }

            return best;
        }

        private static bool showLookRay = true;
        private static float lookRayDrawDistance = 1800f;
		
        private static VertexPositionColor[] fpDebugRayLineVerts = new VertexPositionColor[2];
        private static VertexPositionColor[] fpDebugRayHeadCrossVerts = new VertexPositionColor[4];
        private static VertexPositionColor[] fpDebugRayEndCrossVerts = new VertexPositionColor[4];
        
        private static void DrawDebugLookRay3D()
        {
            if (!showLookRay || !isFirstPerson) return;
            if (Character.Controlled == null) return;
        
            Vector3 screenRayOrigin3, screenRayDir3;
            if (!TryGetCenterScreenRay3D(out screenRayOrigin3, out screenRayDir3))
            {
                GetCameraVectors(out var cp, out var fwd, out _);
                screenRayOrigin3 = cp;
                screenRayDir3 = Vector3.Normalize(fwd);
            }
        
            // Луч стартует от головы персонажа
            Vector2 head2 = GetHeadPosition();
        
            // Для физики мир 2D: берем XY из 3D-направления
            Vector2 rayDir2 = new Vector2(screenRayDir3.X, screenRayDir3.Y);
            if (rayDir2.LengthSquared() < 0.000001f)
            {
                rayDir2 = (lockedDirection == Direction.Right) ? Vector2.UnitX : -Vector2.UnitX;
            }
            else
            {
                rayDir2.Normalize();
            }
        
            float maxDist = lookRayDrawDistance;
            Vector2 end2 = head2 + rayDir2 * maxDist;
            Color rayColor = new Color(255, 170, 40, 255); // miss
        
            if (TryRaycastLookTarget(head2, rayDir2, maxDist, out var hitEntity, out var hitPoint))
            {
                end2 = hitPoint;
                rayColor = new Color(70, 255, 120, 255); // hit
            }
        
            // Стартовый Z берем из экранного луча, чтобы не было "далекой синей точки"
            Vector3 rayStart3 = new Vector3(head2.X, head2.Y, screenRayOrigin3.Z);
        
            // Делаем конец с учетом pitch (по Z)
            float xyLen = Vector2.Distance(head2, end2);
            float zEnd = rayStart3.Z + screenRayDir3.Z * xyLen;
            Vector3 rayEnd3 = new Vector3(end2.X, end2.Y, zEnd);
        
            EnsureResources();
            SetupBasicEffect3D();
        
            var gd = GameMain.Instance.GraphicsDevice;
            gd.DepthStencilState = DepthStencilState.None; // debug всегда виден
            gd.RasterizerState = RasterizerState.CullNone;
            gd.BlendState = BlendState.NonPremultiplied;
        
            basicEffect.TextureEnabled = false;
            basicEffect.VertexColorEnabled = true;
            basicEffect.World = Matrix.Identity;
        
            // Линия луча (локальные массивы, чтобы не конфликтовать с полями класса)
            var lineVerts = new VertexPositionColor[2];
            lineVerts[0] = new VertexPositionColor(rayStart3, rayColor);
            lineVerts[1] = new VertexPositionColor(rayEnd3, rayColor);
        
            foreach (var pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.LineList, lineVerts, 0, 1);
            }
        
            // Крест в конце луча
            float es = 10f;
            var endCross = new VertexPositionColor[4];
            endCross[0] = new VertexPositionColor(new Vector3(rayEnd3.X - es, rayEnd3.Y, rayEnd3.Z), rayColor);
            endCross[1] = new VertexPositionColor(new Vector3(rayEnd3.X + es, rayEnd3.Y, rayEnd3.Z), rayColor);
            endCross[2] = new VertexPositionColor(new Vector3(rayEnd3.X, rayEnd3.Y - es, rayEnd3.Z), rayColor);
            endCross[3] = new VertexPositionColor(new Vector3(rayEnd3.X, rayEnd3.Y + es, rayEnd3.Z), rayColor);
        
            foreach (var pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserPrimitives(PrimitiveType.LineList, endCross, 0, 2);
            }
        
            basicEffect.TextureEnabled = true;
            basicEffect.VertexColorEnabled = true;
        }


        private static bool IsBlockingStructureForLookRay(Structure st)
        {
            if (st == null || st.Removed) return true;

            // Корпус/обшивка/стены/пол/потолок — не считаем интеракт-целями
            if (IsSolidWall(st)) return true;
            if (IsSolidFloorCeiling(st)) return true;
            if (IsShellLikeById(st)) return true;

            // Можно добавить явные исключения по id, если нужно
            return false;
        }
		
        private static bool TryGetCenterScreenRay2D(out Vector2 rayStart, out Vector2 rayDir)
        {
            rayStart = Vector2.Zero;
            rayDir = Vector2.UnitX;
        
            try
            {
                GetCameraVectors(out Vector3 cameraPos, out Vector3 forward, out Vector3 up);
        
                Matrix view = Matrix.CreateLookAt(cameraPos, cameraPos + forward * 100f, up);
                Matrix projection = Matrix.CreatePerspectiveFieldOfView(
                    fov,
                    (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight,
                    1f,
                    50000f);
        
                var vp = GameMain.Instance.GraphicsDevice.Viewport;
        
                Vector3 nearP = vp.Unproject(
                    new Vector3(vp.Width * 0.5f, vp.Height * 0.5f, 0f),
                    projection,
                    view,
                    Matrix.Identity);
        
                Vector3 farP = vp.Unproject(
                    new Vector3(vp.Width * 0.5f, vp.Height * 0.5f, 1f),
                    projection,
                    view,
                    Matrix.Identity);
        
                Vector3 d3 = farP - nearP;
                Vector2 d2 = new Vector2(d3.X, d3.Y);
        
                if (d2.LengthSquared() < 0.000001f)
                {
                    d2 = (lockedDirection == Direction.Right) ? Vector2.UnitX : -Vector2.UnitX;
                }
                else
                {
                    d2.Normalize();
                }
        
                rayStart = new Vector2(nearP.X, nearP.Y);
                rayDir = d2;
                return true;
            }
            catch
            {
                return false;
            }
        }
		
		
		
		

        private static VertexPositionColor[] debugRayHitCrossVerts = new VertexPositionColor[6];
		
        private static void UpdateFirstPersonCursorTarget()
        {
            var ch = Character.Controlled;
            if (ch == null) return;

            try
            {
                ch.CursorPosition = GetFirstPersonCursorWorldPos(fpLookMaxDist);
            }
            catch
            {
                float cursorDir = (lockedDirection == Direction.Right) ? 1f : -1f;
                ch.CursorPosition = ch.Position + new Vector2(cursorDir * 500f, 0f);
                fpLookTarget = null;
            }
        }

        private static void LogShellDebugInfo(Structure st, string prefix = "ShellDebug")
        {
            if (st == null)
            {
                DebugConsole.NewMessage($"{prefix}: target = null", Color.Orange);
                return;
            }

            string id = "unknown";
            try { id = st.Prefab?.Identifier.ToString() ?? "unknown"; } catch { }

            var wr = st.WorldRect;
            float rotDeg = 0f;
            float bodyRotDeg = 0f;
            try { rotDeg = MathHelper.ToDegrees(st.RotationRad); } catch { }
            try { bodyRotDeg = GetSpecialShellBodyRotDeg(st); } catch { }

            bool special = IsSpecialAngledShell(st);
            bool angled = IsAngledStructure(st);
            bool shell = IsShellLikeById(st);

            bool idFront = IsFrontById(st);
            bool idTail = IsTailById(st);

            bool inferredFront = InferIsFrontByPosition(st);
            bool rearByPos = !inferredFront;

            bool lower = IsLowerShellPart(st);

            var settings = GetAngledModeSettings();
            int mirrorMode = settings.mirrorMode;

            bool baseMirror = ResolveMirrorX(st, mirrorMode);
            bool finalMirror = ResolveSpecialShellMirrorX(st, mirrorMode);

            DebugConsole.NewMessage($"[{prefix}] id={id}", Color.Cyan);
            DebugConsole.NewMessage(
                $"  special={special} angled={angled} shell={shell} | idFront={idFront} idTail={idTail} inferredFront={inferredFront} rearByPos={rearByPos} lower={lower}",
                Color.White);
            DebugConsole.NewMessage(
                $"  rotDeg={rotDeg:F2} bodyRotDeg={bodyRotDeg:F2} flipX={st.FlippedX} flipY={st.FlippedY} horiz={st.IsHorizontal}",
                Color.White);
            DebugConsole.NewMessage(
                $"  worldRect=({wr.X},{wr.Y},{wr.Width},{wr.Height})",
                Color.LightGray);
            DebugConsole.NewMessage(
                $"  mirrorBase={baseMirror} mirrorFinal={finalMirror} | debugTarget={(ReferenceEquals(st, debugShellTarget) ? "YES" : "NO")} debugInvert={debugShellTargetMirrorInvert}",
                Color.Yellow);
        }
		
		
        private static int shellMirrorFixMode = 1;
        private const int SHELL_MIRROR_FIX_MODE_COUNT = 9;

        private static string GetShellMirrorFixModeName(int mode) => mode switch
        {
            0 => "Base mirror (old)",
            1 => "Rear only invert (auto front/rear by X)",
            2 => "Rear invert XOR FlippedY",
            3 => "Rear invert XOR !FlippedY",
            4 => "Rear only invert (id front/tail only)",
            5 => "Rear invert + lower(in world Y)",
            6 => "Global invert",
            7 => "Rear invert XOR FlippedX",
            8 => "Rear invert XOR FlippedX XOR FlippedY",
            _ => "Unknown"
        };
        private static bool IsShellLikeById(Structure st)
        {
            string id = GetStructureIdLowerSafe(st);
            return id.Contains("shell") || id.Contains("shuttle") || id.Contains("front") || id.Contains("tail") || id.Contains("bumper");
        }
		
		
        private static bool TryGetSubFrontDirectionIsRight(Submarine sub, out bool frontIsRight)
        {
            frontIsRight = true;
            if (sub == null) return false;

            float frontAvg = 0f;
            float tailAvg = 0f;
            int frontCount = 0;
            int tailCount = 0;

            foreach (var st in Structure.WallList)
            {
                if (st == null || st.Removed || st.Submarine != sub) continue;

                string id = GetStructureIdLowerSafe(st);
                float cx = st.WorldRect.X + st.WorldRect.Width * 0.5f;

                if (id.Contains("front"))
                {
                    frontAvg += cx;
                    frontCount++;
                }
                else if (id.Contains("tail") || id.Contains("fin"))
                {
                    tailAvg += cx;
                    tailCount++;
                }
            }

            if (frontCount > 0 && tailCount > 0)
            {
                frontAvg /= frontCount;
                tailAvg /= tailCount;
                frontIsRight = frontAvg >= tailAvg;
                return true;
            }

            return false;
        }
        private static bool InferIsFrontByPosition(Structure st)
        {
            if (st?.Submarine == null) return true;

            var sub = st.Submarine;

            float minX = float.MaxValue;
            float maxX = float.MinValue;

            foreach (var s in Structure.WallList)
            {
                if (s == null || s.Removed || s.Submarine != sub) continue;
                if (!IsShellLikeById(s)) continue;

                var wr = s.WorldRect;
                float l = wr.X;
                float r = wr.X + wr.Width;

                if (l < minX) minX = l;
                if (r > maxX) maxX = r;
            }

            if (minX >= maxX)
            {
                // fallback: считаем "вправо = front"
                return true;
            }

            float midX = (minX + maxX) * 0.5f;
            float cxSt = st.WorldRect.X + st.WorldRect.Width * 0.5f;

            bool frontIsRight;
            if (!TryGetSubFrontDirectionIsRight(sub, out frontIsRight))
            {
                frontIsRight = true;
            }

            return frontIsRight ? (cxSt >= midX) : (cxSt < midX);
        }

		
		
		
        private static void LogAllSubWallsDebug(bool onlyShellLike, bool dumpAllFixModes)
        {
            try
            {
                var ch = Character.Controlled;
                var sub = ch?.Submarine;

                if (sub == null)
                {
                    DebugConsole.NewMessage("WallDebug: controlled submarine is null", Color.Orange);
                    return;
                }

                string GetIdSafe(Structure st)
                {
                    try { return st?.Prefab?.Identifier.ToString().ToLowerInvariant() ?? ""; }
                    catch { return ""; }
                }

                bool IsShellLikeByIdLocal(Structure st)
                {
                    string id = GetIdSafe(st);
                    return id.Contains("shell") || id.Contains("shuttle") || id.Contains("front") || id.Contains("tail") || id.Contains("bumper");
                }

                bool IsFrontByIdLocal(Structure st)
                {
                    string id = GetIdSafe(st);
                    return id.Contains("front");
                }

                bool IsTailByIdLocal(Structure st)
                {
                    string id = GetIdSafe(st);
                    return id.Contains("tail") || id.Contains("fin");
                }

                float NormalizeDeg180Local(float deg)
                {
                    float d = deg % 360f;
                    if (d > 180f) d -= 360f;
                    if (d < -180f) d += 360f;
                    return d;
                }

                bool TryGetSubFrontDirectionIsRightLocal(Submarine s, out bool frontIsRight)
                {
                    frontIsRight = true;
                    if (s == null) return false;

                    float frontAvg = 0f;
                    float tailAvg = 0f;
                    int frontCount = 0;
                    int tailCount = 0;

                    foreach (var w in Structure.WallList)
                    {
                        if (w == null || w.Removed || w.Submarine != s) continue;
                        string id = GetIdSafe(w);
                        float cx = w.WorldRect.X + w.WorldRect.Width * 0.5f;

                        if (id.Contains("front"))
                        {
                            frontAvg += cx;
                            frontCount++;
                        }
                        else if (id.Contains("tail") || id.Contains("fin"))
                        {
                            tailAvg += cx;
                            tailCount++;
                        }
                    }

                    if (frontCount > 0 && tailCount > 0)
                    {
                        frontAvg /= frontCount;
                        tailAvg /= tailCount;
                        frontIsRight = frontAvg >= tailAvg;
                        return true;
                    }

                    return false;
                }

                bool InferIsFrontByPositionLocal(Structure st)
                {
                    if (st?.Submarine == null) return true;

                    var s = st.Submarine;

                    float minX = float.MaxValue;
                    float maxX = float.MinValue;

                    foreach (var w in Structure.WallList)
                    {
                        if (w == null || w.Removed || w.Submarine != s) continue;
                        if (!IsShellLikeByIdLocal(w)) continue;

                        var wr2 = w.WorldRect;
                        float l = wr2.X;
                        float r = wr2.X + wr2.Width;

                        if (l < minX) minX = l;
                        if (r > maxX) maxX = r;
                    }

                    if (minX >= maxX) return true;

                    float midX = (minX + maxX) * 0.5f;
                    float cxSt = st.WorldRect.X + st.WorldRect.Width * 0.5f;

                    bool frontIsRight;
                    if (!TryGetSubFrontDirectionIsRightLocal(s, out frontIsRight))
                    {
                        frontIsRight = true;
                    }

                    return frontIsRight ? (cxSt >= midX) : (cxSt < midX);
                }

                bool ResolveMirrorByFixModeLocal(Structure st, int mirrorMode, int fixMode)
                {
                    bool baseMirror = ResolveMirrorX(st, mirrorMode);

                    bool idFront = IsFrontByIdLocal(st);
                    bool idTail = IsTailByIdLocal(st);

                    bool inferredFront = InferIsFrontByPositionLocal(st);
                    bool rearByPos = !inferredFront;

                    bool lowerByWorld = IsLowerShellPartStable(st);
                    bool flipX = st?.FlippedX ?? false;
                    bool flipY = st?.FlippedY ?? false;

                    bool rear = idTail ? true : (idFront ? false : rearByPos);

                    return fixMode switch
                    {
                        0 => baseMirror,
                        1 => rear ? !baseMirror : baseMirror,
                        2 => (rear ? !baseMirror : baseMirror) ^ flipY,
                        3 => (rear ? !baseMirror : baseMirror) ^ !flipY,
                        4 => idTail ? !baseMirror : baseMirror,
                        5 => (rear ? !baseMirror : baseMirror) ^ lowerByWorld,
                        6 => !baseMirror,
                        7 => (rear ? !baseMirror : baseMirror) ^ flipX,
                        8 => ((rear ? !baseMirror : baseMirror) ^ flipX) ^ flipY,
                        _ => baseMirror
                    };
                }

                var walls = Structure.WallList
                    .Where(st => st != null && !st.Removed && st.Submarine == sub)
                    .ToList();

                if (onlyShellLike)
                {
                    walls = walls.Where(st => IsShellLikeByIdLocal(st)).ToList();
                }

                walls = walls
                    .OrderBy(st => GetIdSafe(st))
                    .ThenBy(st => st.WorldRect.Y)
                    .ThenBy(st => st.WorldRect.X)
                    .ToList();

                DebugConsole.NewMessage("===== WALL DEBUG START =====", Color.Yellow);
                DebugConsole.NewMessage(
                    $"sub={sub.Info?.Name ?? "unknown"} | total={walls.Count} | onlyShell={onlyShellLike} | fixMode={shellMirrorFixMode} ({GetShellMirrorFixModeName(shellMirrorFixMode)})",
                    Color.Cyan);

                var settings = GetAngledModeSettings();
                int mirrorMode = settings.mirrorMode;

                int shown = 0;
                foreach (var st in walls)
                {
                    string id = GetIdSafe(st);

                    bool shell = IsShellLikeByIdLocal(st);
                    bool special = IsSpecialAngledShell(st);
                    bool angled = IsAngledStructure(st);

                    bool idFront = IsFrontByIdLocal(st);
                    bool idTail = IsTailByIdLocal(st);
                    bool inferredFront = InferIsFrontByPositionLocal(st);
                    bool rearByPos = !inferredFront;

                    bool lower = IsLowerShellPart(st);
                    bool flipY = st.FlippedY;

                    float rotDeg = 0f;
                    float bodyDeg = 0f;
                    try { rotDeg = MathHelper.ToDegrees(st.RotationRad); } catch { }
                    try { bodyDeg = GetSpecialShellBodyRotDeg(st); } catch { }

                    float bodyNorm = NormalizeDeg180Local(bodyDeg);
                    float nearest90 = MathF.Round(bodyNorm / 90f) * 90f;
                    float off90 = bodyNorm - nearest90;

                    bool baseMirror = ResolveMirrorX(st, mirrorMode);
                    bool finalMirror = ResolveSpecialShellMirrorX(st, mirrorMode);

                    var wr = st.WorldRect;
                    float cx = wr.X + wr.Width * 0.5f;

                    float minShellX = float.MaxValue;
                    float maxShellX = float.MinValue;
                    foreach (var s2 in Structure.WallList)
                    {
                        if (s2 == null || s2.Removed || s2.Submarine != sub) continue;
                        string id2 = "";
                        try { id2 = s2.Prefab?.Identifier.ToString().ToLowerInvariant() ?? ""; } catch { }
                        bool shellLike2 = id2.Contains("shell") || id2.Contains("shuttle") || id2.Contains("front") || id2.Contains("tail") || id2.Contains("bumper");
                        if (!shellLike2) continue;

                        var wr2 = s2.WorldRect;
                        minShellX = MathF.Min(minShellX, wr2.X);
                        maxShellX = MathF.Max(maxShellX, wr2.X + wr2.Width);
                    }

                    float midX = (minShellX < maxShellX) ? (minShellX + maxShellX) * 0.5f : cx;
                    bool crossesMid = wr.X <= midX && (wr.X + wr.Width) >= midX;

                    DebugConsole.NewMessage(
                        $"[{shown:000}] id={id} | shell={shell} special={special} angled={angled} | idFront={idFront} idTail={idTail} inferredFront={inferredFront} rearByPos={rearByPos} lower={lower} flipX={st.FlippedX} flipY={st.FlippedY} | cx={cx:F1} midX={midX:F1} crossMid={crossesMid}",
                        shell ? Color.Lime : Color.Gray);

                    DebugConsole.NewMessage(
                        $"      rotDeg={rotDeg:F2} | bodyDeg={bodyDeg:F2} norm={bodyNorm:F2} off90={off90:F2} | flipX={st.FlippedX} flipY={st.FlippedY} horiz={st.IsHorizontal}",
                        Color.White);

                    DebugConsole.NewMessage(
                        $"      worldRect=({wr.X},{wr.Y},{wr.Width},{wr.Height}) | mirrorBase={baseMirror} -> mirrorFinal={finalMirror}",
                        Color.LightGray);

                    if (dumpAllFixModes && shell)
                    {
                        string modesLine = "      modes: ";
                        for (int m = 0; m < SHELL_MIRROR_FIX_MODE_COUNT; m++)
                        {
                            bool mm = ResolveMirrorByFixModeLocal(st, mirrorMode, m);
                            modesLine += $"[{m}:{(mm ? "M" : "N")}] ";
                        }
                        DebugConsole.NewMessage(modesLine, Color.Orange);
                    }

                    shown++;
                    if (shown >= 260)
                    {
                        DebugConsole.NewMessage("WallDebug: output truncated at 260 entries", Color.Orange);
                        break;
                    }
                }

                DebugConsole.NewMessage("===== WALL DEBUG END =====", Color.Yellow);
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage("WallDebug error: " + ex.Message, Color.Red);
            }
        }

        private static bool IsFrontShellPart(Structure st)
        {
            string id = GetStructureIdLower(st);
            return id.Contains("front");
        }
		
		
        private static bool IsTailShellPart(Structure st)
        {
            string id = GetStructureIdLower(st);
            return id.Contains("tail") || id.Contains("fin");
        }
		
		
        private static bool ResolveSpecialShellMirrorX(Structure st, int mirrorMode)
        {
            bool baseMirror = ResolveMirrorX(st, mirrorMode);
            if (st?.Submarine == null) return baseMirror;

            string id = "";
            try { id = st.Prefab?.Identifier.ToString().ToLowerInvariant() ?? ""; } catch { }

            bool idFront = IsFrontById(st);
            bool idTail = IsTailById(st);

            var sub = st.Submarine;

            float minShellX = float.MaxValue;
            float maxShellX = float.MinValue;

            foreach (var s2 in Structure.WallList)
            {
                if (s2 == null || s2.Removed || s2.Submarine != sub) continue;
                if (!IsShellLikeById(s2)) continue;

                var wr2 = s2.WorldRect;
                minShellX = MathF.Min(minShellX, wr2.X);
                maxShellX = MathF.Max(maxShellX, wr2.X + wr2.Width);
            }

            if (minShellX >= maxShellX) return baseMirror;

            var wr = st.WorldRect;
            float cx = wr.X + wr.Width * 0.5f;
            float midX = (minShellX + maxShellX) * 0.5f;

            bool crossesMid = wr.X <= midX && (wr.X + wr.Width) >= midX;

            bool frontIsRight;
            if (!TryGetSubFrontDirectionIsRight(sub, out frontIsRight))
            {
                frontIsRight = true;
            }

            bool rearByPos = frontIsRight ? (cx < midX) : (cx >= midX);
            bool inferredFront = !rearByPos;

            // ВАЖНО: стабильный lower, не зависящий от текущей комнаты/камеры
            bool lowerByWorld = IsLowerShellPartStable(st);

            bool flipX = st.FlippedX;
            bool flipY = st.FlippedY;

            bool rear;
            if (idTail) rear = true;
            else if (idFront) rear = false;
            else if (crossesMid) rear = false; // длинные секции через центр оставляем по базе
            else rear = rearByPos;

            // Базовая логика
            bool result = rear ? !baseMirror : baseMirror;

            // ===== Точечные подтвержденные кейсы по твоим логам =====

            // extA_Front у тебя корректен только в инверте
            if (id.Contains("exta_front"))
            {
                result = !baseMirror;
            }

            // shella42deg (оба варианта flipX) — правильный только в инверте
            if (id.Contains("shella42deg"))
            {
                result = !baseMirror;
            }

            // shellacombo12 — оба кейса у тебя правильные только в инверте
            if (id.Contains("shellacombo12"))
            {
                result = !baseMirror;
            }

            // задние нижние "горки" (ты подтвердил)
            if ((id.Contains("shella17deg") || id.Contains("shella14degb")) && flipY && !lowerByWorld)
            {
                result = !baseMirror;
            }

            // нижняя длинная shella0deg (flipY=true, lower=false)
            if (id.Contains("shella0deg") && flipY && !lowerByWorld)
            {
                result = !baseMirror;
            }

            // дополнительный fallback для нижних диагоналей
            if (!lowerByWorld && flipY && (id.Contains("shella14") || id.Contains("shella17")))
            {
                result = !baseMirror;
            }

            // ===== DEV override (K) для выбранной структуры =====
            bool isDebugTarget =
                (debugShellTargetId != Entity.NullEntityID && st.ID == debugShellTargetId) ||
                ReferenceEquals(st, debugShellTarget);

            if (isDebugTarget && debugShellTargetMirrorInvert)
            {
                result = !result;
            }

            return result;
        }
	
        private static void DrawWallSurface(
            SpriteBatch sb,
            Texture2D tex,
            Rectangle src,
            Rectangle dest,
            Color tint,
            float scale,
            bool useBottomStrip,
            int phaseX = 0,
            int phaseY = 0)
        {
            if (sb == null || tex == null || tex.IsDisposed) return;
            if (dest.Width <= 0 || dest.Height <= 0) return;
            if (src.Width <= 0 || src.Height <= 0) return;

            // Фикс: для обычных стен/пола/потолка используем center band stretched
            Rectangle band = GetCenterBand(src);

            sb.Draw(
                tex,
                dest,
                band,
                tint);
        }
		
        private static bool TryGetArmAttachedPosition(Limb limb, float yOffset, out Vector2 attachedPos)
        {
            attachedPos = Vector2.Zero;
        
            var parentType = GetParentArmLimbType(limb.type);
            if (parentType == null) return false;
        
            Limb parent = FindLimbByType(limb.character, parentType.Value);
            if (parent?.body == null) return false;
        
            Sprite parentSprite = GetLimbSprite(parent);
            if (parentSprite?.Texture == null || parentSprite.Texture.IsDisposed) return false;
        
            float pScale = parent.Scale * parent.TextureScale;
            if (pScale < 0.01f) pScale = 1f;
        
            float pr = -parent.body.DrawRotation;
            Vector2 pPos = new Vector2(parent.body.DrawPosition.X, parent.body.DrawPosition.Y + yOffset);
            Vector2 childCurrent = new Vector2(limb.body.DrawPosition.X, limb.body.DrawPosition.Y + yOffset);
        
            // Оба конца родителя в локальных координатах (как в DrawExtrudedMesh)
            float px0 = -parentSprite.Origin.X;
            float px1 = parentSprite.SourceRect.Width - parentSprite.Origin.X;
            float pyMid = parentSprite.Origin.Y - parentSprite.SourceRect.Height * 0.5f;
        
            Vector2 endA = pPos + RotateLocal(px0 * pScale, pyMid * pScale, pr);
            Vector2 endB = pPos + RotateLocal(px1 * pScale, pyMid * pScale, pr);
        
            // Берем ближайший к текущей позиции дочерней части
            float dA = Vector2.DistanceSquared(childCurrent, endA);
            float dB = Vector2.DistanceSquared(childCurrent, endB);
        
            attachedPos = (dA <= dB) ? endA : endB;
            return true;
        }
        private static void DrawSurfaceStripTiledXZ(
            SpriteBatch sb,
            Texture2D tex,
            Rectangle srcStrip,
            Rectangle dest,
            Color tint,
            float scale,
            int phaseX = 0,
            int phaseZ = 0)
        {
            if (dest.Width <= 0 || dest.Height <= 0 || tex == null || tex.IsDisposed) return;
        
            int tileW = Math.Max((int)(srcStrip.Width * scale), 1);
            int tileH = Math.Max((int)(srcStrip.Height * scale), 1);
        
            int startX = dest.X - MathUtils.PositiveModulo(dest.X + phaseX, tileW);
            int startZ = dest.Y - MathUtils.PositiveModulo(dest.Y + phaseZ, tileH);
        
            for (int x = startX; x < dest.Right; x += tileW)
            {
                int drawX = Math.Max(x, dest.X);
                int drawW = Math.Min(x + tileW, dest.Right) - drawX;
                if (drawW <= 0) continue;
        
                int srcX = srcStrip.X + (int)((drawX - x) / scale);
                int srcW = Math.Max(1, (int)MathF.Ceiling(drawW / scale));
                if (srcX + srcW > srcStrip.Right) srcW = Math.Max(1, srcStrip.Right - srcX);
        
                for (int z = startZ; z < dest.Bottom; z += tileH)
                {
                    int drawZ = Math.Max(z, dest.Y);
                    int drawH = Math.Min(z + tileH, dest.Bottom) - drawZ;
                    if (drawH <= 0) continue;
        
                    int srcY = srcStrip.Y + (int)((drawZ - z) / scale);
                    int srcH = Math.Max(1, (int)MathF.Ceiling(drawH / scale));
                    if (srcY + srcH > srcStrip.Bottom) srcH = Math.Max(1, srcStrip.Bottom - srcY);
        
                    sb.Draw(
                        tex,
                        new Rectangle(drawX, drawZ, drawW, drawH),
                        new Rectangle(srcX, srcY, srcW, srcH),
                        tint);
                }
            }
        }
       
        private static int shellFloorCeilingMode = 0;
        private const int SHELL_FLOOR_CEILING_MODE_COUNT = 5;

        private static string GetShellFloorCeilingModeName(int mode) => mode switch
        {
            0 => "Shell: Full texture stretched",
            1 => "Shell: Center band stretched",
            2 => "Shell: Surface strip stretched",
            3 => "Shell: Surface strip tiled",
            4 => "Shell: Solid color",
            _ => "Unknown"
        };
        private static bool IsShellLikeStructure(Structure st)
        {
            if (st?.Prefab == null) return false;

            string id = "";
            string name = "";

            try { id = st.Prefab.Identifier.ToString().ToLowerInvariant(); } catch { }
            try { name = st.Name?.ToLowerInvariant() ?? ""; } catch { }

            // Shell / shuttle / front / tail из твоих списков
            if (id.Contains("shell")) return true;
            if (id.Contains("shuttle")) return true;
            if (id.Contains("front")) return true;
            if (id.Contains("tail")) return true;
            if (id.Contains("bumper")) return true;

            if (name.Contains("submarine wall")) return true;
            if (name.Contains("shuttle wall")) return true;
            if (name.Contains("submarine front")) return true;
            if (name.Contains("submarine tail")) return true;

            return false;
        }



        private static bool IsSpecialAngledShell(Structure st)
        {
            if (st?.Prefab == null) return false;

            string id = "";
            string name = "";
            try { id = st.Prefab.Identifier.ToString().ToLowerInvariant(); } catch { }
            try { name = st.Name?.ToLowerInvariant() ?? ""; } catch { }

            bool shellLike =
                id.Contains("shell") ||
                id.Contains("shuttle") ||
                id.Contains("front") ||
                id.Contains("tail") ||
                id.Contains("bumper") ||
                id.Contains("exta_front") ||
                id.Contains("exta_tail") ||
                id.Contains("sshellaglass") ||   // <--- важно для sshellaglassf
                id.Contains("miscstructures");

            if (!shellLike) return false;

            float bodyRotDeg = 0f;
            try { bodyRotDeg = st.Prefab.BodyRotation; } catch { }

            // Явные shell-особые куски: всегда special
            if (id.Contains("exta_front") || id.Contains("exta_tail") || id.Contains("sshellaglass"))
                return true;

            if (MathF.Abs(bodyRotDeg % 90f) > 1.5f) return true;
            if (id.Contains("curve") || id.Contains("combo") || id.Contains("cap")) return true;
            if (id.Contains("deg") || name.Contains("degrees")) return true;

            return false;
        }	
		
        private static void DrawTiledContinuousFlippedY(
            SpriteBatch sb,
            Texture2D tex,
            Rectangle src,
            Rectangle dest,
            Color tint,
            float scale,
            int phaseX = 0,
            int phaseY = 0)
        {
            if (dest.Width <= 0 || dest.Height <= 0 || tex == null || tex.IsDisposed) return;

            int tileW = Math.Max((int)(src.Width * scale), 1);
            int tileH = Math.Max((int)(src.Height * scale), 1);

            int startX = dest.X - MathUtils.PositiveModulo(dest.X + phaseX, tileW);
            int startY = dest.Y - MathUtils.PositiveModulo(dest.Y + phaseY, tileH);

            for (int x = startX; x < dest.Right; x += tileW)
            {
                int drawX = Math.Max(x, dest.X);
                int drawW = Math.Min(x + tileW, dest.Right) - drawX;
                if (drawW <= 0) continue;

                int srcX = src.X + (int)((drawX - x) / scale);
                int srcW = Math.Max(1, (int)(drawW / scale));

                for (int y = startY; y < dest.Bottom; y += tileH)
                {
                    int drawY = Math.Max(y, dest.Y);
                    int drawH = Math.Min(y + tileH, dest.Bottom) - drawY;
                    if (drawH <= 0) continue;

                    int srcY = src.Y + (int)((drawY - y) / scale);
                    int srcH = Math.Max(1, (int)(drawH / scale));

                    sb.Draw(
                        tex,
                        new Rectangle(drawX, drawY, drawW, drawH),
                        new Rectangle(srcX, srcY, srcW, srcH),
                        tint,
                        0f,
                        Vector2.Zero,
                        SpriteEffects.FlipVertically,
                        0f);
                }
            }
        }

        private static void DrawSpecialShellExtruded(
            Structure st,
            float thickness,
            byte alphaThreshold,
            bool drawCaps,
            bool drawSides,
            bool mirrorX = true,
            float uvInsetPx = 0.5f,
            bool opaqueSides = false)
        {
            if (st == null) return;
            if (!GetTexture(st, out var tex, out var src, out float _)) return;
            if (tex == null || tex.IsDisposed || src.Width <= 0 || src.Height <= 0) return;

            EnsureResources();
            SetupBasicEffect3D();

            var gd = GameMain.Instance.GraphicsDevice;

            var mesh = GetOrBuildExtrudedMesh(tex, src, alphaThreshold);
            if (mesh == null && drawSides) return;

            var wr = st.WorldRect;

            float cx = wr.X + wr.Width * 0.5f;
            float cy = wr.Y - wr.Height * 0.5f;

            float rot = -st.RotationRad;
            float sx = Math.Max(0.01f, wr.Width / (float)src.Width);
            float sy = Math.Max(0.01f, wr.Height / (float)src.Height);

            // Учитываем флипы структуры
            if (st.FlippedX) sx = -sx;
            if (st.FlippedY) sy = -sy;

            // Твой mirrorX режим поверх
            if (mirrorX) sx = -sx;

            float halfDepth = Math.Max(2f, thickness * 0.5f);

            float x0 = -src.Width * 0.5f;
            float x1 = +src.Width * 0.5f;
            float y0 = +src.Height * 0.5f;
            float y1 = -src.Height * 0.5f;

            float texW = tex.Width;
            float texH = tex.Height;

            float uInset = uvInsetPx / texW;
            float vInset = uvInsetPx / texH;

            float u0 = src.X / texW + uInset;
            float v0 = src.Y / texH + vInset;
            float u1 = (src.X + src.Width) / texW - uInset;
            float v1 = (src.Y + src.Height) / texH - vInset;

            // Для flipY важно перевернуть V, иначе "низ как верх"
            if (st.FlippedY)
            {
                float tv = v0;
                v0 = v1;
                v1 = tv;
            }

            Color frontColor = MulOpaque(st.SpriteColor, 1.00f);
            Color backColor = MulOpaque(st.SpriteColor, 0.80f);

            Matrix world =
                Matrix.CreateScale(sx, sy, 1f) *
                Matrix.CreateRotationZ(rot) *
                Matrix.CreateTranslation(cx, cy, 0f);

            Vector3 frontTL = new Vector3(x0, y0, +halfDepth);
            Vector3 frontTR = new Vector3(x1, y0, +halfDepth);
            Vector3 frontBL = new Vector3(x0, y1, +halfDepth);
            Vector3 frontBR = new Vector3(x1, y1, +halfDepth);

            Vector3 backTL = new Vector3(x0, y0, -halfDepth);
            Vector3 backTR = new Vector3(x1, y0, -halfDepth);
            Vector3 backBL = new Vector3(x0, y1, -halfDepth);
            Vector3 backBR = new Vector3(x1, y1, -halfDepth);

            gd.DepthStencilState = DepthStencilState.Default;
            gd.RasterizerState = RasterizerState.CullNone;
            gd.SamplerStates[0] = SamplerState.PointClamp;
            gd.BlendState = BlendState.NonPremultiplied;

            if (drawCaps)
            {
                DrawTexturedFaceAlphaTest(
                    gd,
                    tex,
                    world,
                    backTR, backTL, backBR, backBL,
                    u0, v0, u1, v1,
                    backColor);
            }

            if (drawSides &&
                mesh?.SideVertices != null && mesh.SideIndices != null &&
                mesh.SideVertices.Length > 0 && mesh.SideIndices.Length > 0)
            {
                Matrix sideWorld =
                    Matrix.CreateScale(sx, sy, Math.Max(halfDepth - 0.15f, 0.1f)) *
                    Matrix.CreateRotationZ(rot) *
                    Matrix.CreateTranslation(cx, cy, 0f);

                basicEffect.World = sideWorld;
                basicEffect.Texture = mesh.Texture;
                basicEffect.Alpha = 1f;

                gd.BlendState = opaqueSides ? BlendState.Opaque : BlendState.NonPremultiplied;

                foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(
                        PrimitiveType.TriangleList,
                        mesh.SideVertices, 0, mesh.SideVertices.Length,
                        mesh.SideIndices, 0, mesh.SideIndices.Length / 3);
                }
            }

            if (drawCaps)
            {
                DrawTexturedFaceAlphaTest(
                    gd,
                    tex,
                    world,
                    frontTL, frontTR, frontBL, frontBR,
                    u0, v0, u1, v1,
                    frontColor);
            }

            basicEffect.World = Matrix.Identity;
            gd.BlendState = BlendState.NonPremultiplied;
        }


        // Обычные стены/пол/потолок: фиксируем center band stretched
        private static int wallSurfaceMode = 2;

        // Угловые/диагональные (горки): отдельный режим по L
        private static int angledMode = 1;
        private const int ANGLED_MODE_COUNT = 4;

        private static string GetAngledModeName(int mode) => mode switch
        {
            0 => "Base | alpha 140 | auto mirror",
            1 => "Base no caps | alpha 140 | auto mirror",
            2 => "Base | alpha 140 | auto mirror | opaque sides",
            3 => "Base no caps | alpha 160 | auto mirror | inset 2px",
            _ => "Unknown"
        };
		
		
		
		
		

        private static float GetSpecialShellBodyRotDeg(Structure st)
        {
            try { return st?.Prefab?.BodyRotation ?? 0f; }
            catch { return 0f; }
        }

        private static (bool drawCaps, bool drawSides, int mirrorMode, byte alphaTh, float uvInsetPx, bool opaqueSides) GetAngledModeSettings()
        {
            return angledMode switch
            {
                0 => (true,  true,  0,  220, 0.5f, false), // old 4 spirit
                1 => (false, true,  0, 220, 0.5f, false), // old 14 spirit
                2 => (true,  true,  1, 220, 0.5f, false), // mirror off
                3 => (true,  true,  2, 220, 0.5f, false), // mirror if bodyRot<0
                4 => (true,  true,  3, 220, 0.5f, false), // mirror if bodyRot>0
                5 => (true,  true,  0, 220, 0.5f, true),  // opaque sides
                6 => (true,  true,  0, 220, 0.5f, true),
                7 => (true,  true,  0, 220, 0.5f, true),
                8 => (true,  true,  0, 220, 1.0f, true),  // anti stripes
                9 => (true,  true,  0, 220, 2.0f, true),  // strong anti stripes
                _ => (true,  true,  0, 220, 0.5f, false)
            };
        }

        private static bool ResolveMirrorX(Structure st, int mirrorMode)
        {
            float br = GetSpecialShellBodyRotDeg(st);

            return mirrorMode switch
            {
                0 => true,        // always mirror (текущая рабочая база)
                1 => false,       // never mirror
                2 => br < 0f,     // для нижних/верхних по знаку
                3 => br > 0f,
                _ => true
            };
        }
		
		
		
		
        private static Rectangle GetCenterColumn(Rectangle src)
        {
            int w = Math.Clamp(src.Width / 8, 1, Math.Max(1, src.Width));
            int x = src.X + (src.Width - w) / 2;
            return new Rectangle(x, src.Y, w, src.Height);
        }

        private static Rectangle GetEdgeColumn(Rectangle src, bool left)
        {
            int w = Math.Clamp(src.Width / 10, 1, Math.Max(1, src.Width));
            if (left) return new Rectangle(src.X, src.Y, w, src.Height);
            return new Rectangle(src.Right - w, src.Y, w, src.Height);
        }

        private static Rectangle GetCenterRow(Rectangle src)
        {
            int h = Math.Clamp(src.Height / 8, 1, Math.Max(1, src.Height));
            int y = src.Y + (src.Height - h) / 2;
            return new Rectangle(src.X, y, src.Width, h);
        }

        private static Rectangle GetEdgeRow(Rectangle src, bool top)
        {
            int h = Math.Clamp(src.Height / 10, 1, Math.Max(1, src.Height));
            if (top) return new Rectangle(src.X, src.Y, src.Width, h);
            return new Rectangle(src.X, src.Bottom - h, src.Width, h);
        }
		
        private static void LogAngledDebugInfo()
        {
            try
            {
                int total = angledStructures?.Count ?? 0;
                if (total <= 0)
                {
                    DebugConsole.NewMessage("Angled debug: angledStructures is empty", Color.Orange);
                    return;
                }

                int specialCount = 0;
                for (int i = 0; i < angledStructures.Count; i++)
                {
                    if (IsSpecialAngledShell(angledStructures[i])) specialCount++;
                }

                DebugConsole.NewMessage(
                    $"Angled debug: total={total}, special={specialCount}, mode={angledMode} ({GetAngledModeName(angledMode)})",
                    Color.Cyan);

                int shown = 0;
                for (int i = 0; i < angledStructures.Count && shown < 12; i++)
                {
                    var st = angledStructures[i];
                    if (st == null) continue;

                    string id = "unknown";
                    try { id = st.Prefab?.Identifier.ToString() ?? "unknown"; } catch { }

                    float rotDeg = 0f;
                    float bodyRotDeg = 0f;
                    try { rotDeg = MathHelper.ToDegrees(st.RotationRad); } catch { }
                    try { bodyRotDeg = st.Prefab?.BodyRotation ?? 0f; } catch { }

                    bool special = IsSpecialAngledShell(st);

                    DebugConsole.NewMessage(
                        $"  [{shown}] id={id} | special={special} | rot={rotDeg:F1} | bodyRot={bodyRotDeg:F1}",
                        special ? Color.Lime : Color.Gray);

                    shown++;
                }
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage("Angled debug error: " + ex.Message, Color.Red);
            }
        }
		
        private static Rectangle GetShellCapSourceRect(int shellMode, Rectangle src, bool useBottomStrip)
        {
            return shellMode switch
            {
                0 => src,                                   // full texture
                1 => GetCenterBand(src),                   // center band
                2 => GetSurfaceStrip(src, useBottomStrip), // top/bottom strip
                3 => GetSurfaceStrip(src, useBottomStrip), // strip tiled
                4 => src,                                  // solid color mode (src не важен)
                _ => src
            };
        }
		
		
		
		
        private static void DrawHullBoundaryTexturedFallback(Structure floorRef, Structure ceilRef, bool needFloor, bool needCeiling)
        {
            // В обычном режиме не рисуем длинные фолбэк-плоскости
            if (!xrayMode) return;
        
            int zStart = (int)(-layerDepth);
            int zLen = (int)(layerDepth * 2f);
            int width = Math.Max(1, (int)(roomRight - roomLeft));
            if (width <= 1) return;
        
            // Ceiling: используем ceilRef (а не floorRef)
            if (needCeiling)
            {
                if (ceilRef != null && GetTexture(ceilRef, out var texC, out var srcC, out float scC))
                {
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.Opaque,
                        SamplerState.LinearWrap,
                        depthWriteState,
                        RasterizerState.CullNone,
                        null,
                        BuildFloorMatrix(roomCeiling));
        
                    DrawTiledContinuous(
                        fpBatch,
                        texC,
                        srcC,
                        new Rectangle((int)roomLeft, zStart, width, zLen),
                        Mult(ceilRef.SpriteColor, 0.82f),
                        Math.Max(scC, 0.01f),
                        (int)roomLeft,
                        0);
        
                    fpBatch.End();
                }
                else
                {
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.Opaque,
                        null,
                        depthWriteState,
                        RasterizerState.CullNone,
                        null,
                        BuildFloorMatrix(roomCeiling));
        
                    fpBatch.Draw(pixelTexture, new Rectangle((int)roomLeft, zStart, width, zLen), new Color(90, 100, 115));
                    fpBatch.End();
                }
            }
        
            // Floor: используем floorRef и НЕ flip по Y
            if (needFloor)
            {
                if (floorRef != null && GetTexture(floorRef, out var texF, out var srcF, out float scF))
                {
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.Opaque,
                        SamplerState.LinearWrap,
                        depthWriteState,
                        RasterizerState.CullNone,
                        null,
                        BuildFloorMatrix(roomFloor));
        
                    DrawTiledContinuous(
                        fpBatch,
                        texF,
                        srcF,
                        new Rectangle((int)roomLeft, zStart, width, zLen),
                        Mult(floorRef.SpriteColor, 0.72f),
                        Math.Max(scF, 0.01f),
                        (int)roomLeft,
                        0);
        
                    fpBatch.End();
                }
                else
                {
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.Opaque,
                        null,
                        depthWriteState,
                        RasterizerState.CullNone,
                        null,
                        BuildFloorMatrix(roomFloor));
        
                    fpBatch.Draw(pixelTexture, new Rectangle((int)roomLeft, zStart, width, zLen), new Color(70, 80, 95));
                    fpBatch.End();
                }
            }
        }		
		
		
        private static void DrawBottomCapByMode(
            Structure st,
            Rectangle dest,
            Color tint)
        {
            if (dest.Width <= 0 || dest.Height <= 0) return;

            if (!GetTexture(st, out var tex, out var src, out float sc))
            {
                fpBatch.Draw(pixelTexture, dest, tint);
                return;
            }

            float scale = Math.Max(sc, 0.01f);

            switch (floorBottomMode)
            {
                case 0:
                    DrawTiledContinuous(
                        fpBatch,
                        tex,
                        src,
                        dest,
                        tint,
                        scale,
                        dest.X,
                        0);
                    break;

                case 1:
                    DrawTiledContinuousFlippedY(
                        fpBatch,
                        tex,
                        src,
                        dest,
                        tint,
                        scale,
                        dest.X,
                        0);
                    break;

                case 2:
                {
                    Rectangle strip = GetSurfaceStrip(src, floor: true);
                    DrawSurfaceStripAlongX(
                        fpBatch,
                        tex,
                        strip,
                        dest,
                        tint,
                        scale,
                        phaseX: dest.X);
                    break;
                }

                case 3:
                {
                    Rectangle strip = GetSurfaceStrip(src, floor: false);
                    DrawSurfaceStripAlongX(
                        fpBatch,
                        tex,
                        strip,
                        dest,
                        tint,
                        scale,
                        phaseX: dest.X);
                    break;
                }

                case 4:
                    if (st?.FlippedY ?? false)
                    {
                        fpBatch.Draw(
                            tex,
                            dest,
                            src,
                            tint,
                            0f,
                            Vector2.Zero,
                            SpriteEffects.FlipVertically,
                            0f);
                    }
                    else
                    {
                        fpBatch.Draw(tex, dest, src, tint);
                    }
                    break;

                case 5:
                    fpBatch.Draw(pixelTexture, dest, tint);
                    break;
            }
        }
		
        private static void DrawTiledContinuous(
            SpriteBatch sb,
            Texture2D tex,
            Rectangle src,
            Rectangle dest,
            Color tint,
            float scale,
            int phaseX = 0,
            int phaseY = 0)
        {
            if (dest.Width <= 0 || dest.Height <= 0 || tex == null || tex.IsDisposed) return;
        
            int tileW = Math.Max((int)(src.Width * scale), 1);
            int tileH = Math.Max((int)(src.Height * scale), 1);
        
            // Непрерывная фаза (чтобы не было "полосок" по секциям)
            int startX = dest.X - MathUtils.PositiveModulo(dest.X + phaseX, tileW);
            int startY = dest.Y - MathUtils.PositiveModulo(dest.Y + phaseY, tileH);
        
            for (int x = startX; x < dest.Right; x += tileW)
            {
                int drawX = Math.Max(x, dest.X);
                int drawW = Math.Min(x + tileW, dest.Right) - drawX;
                if (drawW <= 0) continue;
        
                int srcX = src.X + (int)((drawX - x) / scale);
                int srcW = Math.Max(1, (int)(drawW / scale));
        
                for (int y = startY; y < dest.Bottom; y += tileH)
                {
                    int drawY = Math.Max(y, dest.Y);
                    int drawH = Math.Min(y + tileH, dest.Bottom) - drawY;
                    if (drawH <= 0) continue;
        
                    int srcY = src.Y + (int)((drawY - y) / scale);
                    int srcH = Math.Max(1, (int)(drawH / scale));
        
                    sb.Draw(
                        tex,
                        new Rectangle(drawX, drawY, drawW, drawH),
                        new Rectangle(srcX, srcY, srcW, srcH),
                        tint);
                }
            }
        }
        
        private static string GetHandFixModeName(int mode)
        {
            int rotIndex = mode & 3; // 0..3
            bool flipX = (mode & 4) != 0;
            bool flipY = (mode & 8) != 0;
        
            int rotDeg = rotIndex * 90;
            return $"ROT{rotDeg}" +
                   (flipX ? " | FLIP_X" : "") +
                   (flipY ? " | FLIP_Y" : "");
        }

        private static string GetFaceName(int face) => face switch
        {
            0 => "FRONT (передняя)",
            1 => "BACK (задняя)",
            2 => "TOP (верхняя)",
            3 => "BOTTOM (нижняя)",
            4 => "LEFT (левая)",
            5 => "RIGHT (правая)",
            _ => "Unknown"
        };

        private static void SetSelectedOffset(float value)
        {
            switch (limbDisplayMode, selectedSubPart)
            {
                case (1, 0): headOffset = value; break;
                case (2, 0): torsoOffset = value; break;
                case (2, 1): waistOffset = value; break;
                case (3, 0): leftArmOffset = value; break;
                case (3, 1): leftForearmOffset = value; break;
                case (3, 2): leftHandOffset = value; break;
                case (4, 0): rightArmOffset = value; break;
                case (4, 1): rightForearmOffset = value; break;
                case (4, 2): rightHandOffset = value; break;
                case (5, 0): leftThighOffset = value; break;
                case (5, 1): leftLegOffset = value; break;
                case (5, 2): leftFootOffset = value; break;
                case (6, 0): rightThighOffset = value; break;
                case (6, 1): rightLegOffset = value; break;
                case (6, 2): rightFootOffset = value; break;
            }
        }
		
        private static Rectangle GetSurfaceStrip(Rectangle src, bool floor)
        {
            int stripH = Math.Clamp(src.Height / 4, 8, 32);
            if (floor)
            {
                return new Rectangle(src.X, src.Bottom - stripH, src.Width, stripH); // нижняя часть текстуры
            }
            return new Rectangle(src.X, src.Y, src.Width, stripH); // верхняя часть текстуры
        }
		
		
        private static float leftArmPivotShiftY = 10f;
        private static float leftForearmPivotShiftY = 10f;
        private static float leftHandPivotShiftY = 6f;
        
        private static float rightArmPivotShiftY = 10f;
        private static float rightForearmPivotShiftY = 10f;
        private static float rightHandPivotShiftY = 6f;
        private static float GetArmPivotShiftY(LimbType t)
        {
            return t switch
            {
                LimbType.LeftArm => leftArmPivotShiftY,
                LimbType.LeftForearm => leftForearmPivotShiftY,
                LimbType.LeftHand => leftHandPivotShiftY,
        
                LimbType.RightArm => rightArmPivotShiftY,
                LimbType.RightForearm => rightForearmPivotShiftY,
                LimbType.RightHand => rightHandPivotShiftY,
        
                _ => 0f
            };
        }
		
        
        private static bool IsAngledStructure(Structure st)
        {
            if (st == null || !st.HasBody) return false;
        
            // Нормализуем угол
            float r = MathHelper.WrapAngle(st.RotationRad);
        
            // Если угол близок к кратному 90°, считаем обычной структурой
            float step = MathF.PI / 2f;
            float nearest = MathF.Round(r / step) * step;
        
            return MathF.Abs(r - nearest) > 0.01f;
        }



        private static Vector2 leftArmPullOffsetPx = new Vector2(+14f, 0f);
        private static Vector2 leftForearmPullOffsetPx = new Vector2(+14f, 0f);
        private static Vector2 leftHandPullOffsetPx = new Vector2(+10f, 0f);
        
        private static Vector2 rightArmPullOffsetPx = new Vector2(-14f, 0f);
        private static Vector2 rightForearmPullOffsetPx = new Vector2(-14f, 0f);
        private static Vector2 rightHandPullOffsetPx = new Vector2(-10f, 0f);

		
		
        // Доп. поворот в градусах для каждой части рук
        private static float leftArmRotDeg = 0f;
        private static float leftForearmRotDeg = 0f;
        private static float leftHandRotDeg = 0f;
        
        private static float rightArmRotDeg = 0f;
        private static float rightForearmRotDeg = 0f;
        private static float rightHandRotDeg = 0f;
        
        // Если true - дополнительный разворот рук на 180 (твоя текущая идея)
        private static bool armAutoFlip180 = true;
        
        // Если true - правый double-pass включен (он часто ломает одну руку по растягу)
        private static bool useRightArmDoublePass = false;
		
		
        private static float GetArmExtraRotDeg(LimbType t)
        {
            return t switch
            {
                LimbType.LeftArm => leftArmRotDeg,
                LimbType.LeftForearm => leftForearmRotDeg,
                LimbType.LeftHand => leftHandRotDeg,
                LimbType.RightArm => rightArmRotDeg,
                LimbType.RightForearm => rightForearmRotDeg,
                LimbType.RightHand => rightHandRotDeg,
                _ => 0f
            };
        }
		
        
        private static float GetSpecialHalfDepthByMode()
        {
            return angledMode switch
            {
                2 => layerDepth * 0.70f, // narrow
                _ => layerDepth          // остальные от стенки до стенки
            };
        }

        private static void DrawSpecialShellCapsAtDepth(Structure st, float z, SamplerState sampler)
        {
            fpBatch.Begin(
                SpriteSortMode.BackToFront,
                BlendState.NonPremultiplied,
                sampler,
                depthWriteState,
                RasterizerState.CullNone,
                null,
                BuildWallMatrix(z));

            try
            {
                st.Draw(fpBatch, false, true);
                st.Draw(fpBatch, false, false);
            }
            catch { }

            fpBatch.End();
        }

        private static void DrawSpecialShellBridgeQuads(Structure st, float halfDepth)
        {
            if (!GetTexture(st, out var tex, out var src, out float _)) return;
            if (tex == null || tex.IsDisposed || src.Width <= 0 || src.Height <= 0) return;

            var gd = GameMain.Instance.GraphicsDevice;

            // Центр и ориентация: берем body-параметры, а не rect-угол
            Vector2 center = st.WorldPosition;
            try { center += st.BodyOffset * st.Scale; } catch { }

            float rot = 0f;
            try { rot = -st.BodyRotation; } catch { }

            float w = st.WorldRect.Width;
            float h = st.WorldRect.Height;
            try
            {
                if (st.BodyWidth > 0.01f) w = st.BodyWidth;
                if (st.BodyHeight > 0.01f) h = st.BodyHeight;
            }
            catch { }

            float hw = Math.Max(1f, w * 0.5f);
            float hh = Math.Max(1f, h * 0.5f);

            Vector2 Rotate(float lx, float ly)
            {
                float c = MathF.Cos(rot);
                float s = MathF.Sin(rot);
                return new Vector2(center.X + lx * c - ly * s, center.Y + lx * s + ly * c);
            }

            Vector2 tl = Rotate(-hw, +hh);
            Vector2 tr = Rotate(+hw, +hh);
            Vector2 bl = Rotate(-hw, -hh);
            Vector2 br = Rotate(+hw, -hh);

            Vector3 backTL = new Vector3(tl.X, tl.Y, -halfDepth);
            Vector3 backTR = new Vector3(tr.X, tr.Y, -halfDepth);
            Vector3 backBL = new Vector3(bl.X, bl.Y, -halfDepth);
            Vector3 backBR = new Vector3(br.X, br.Y, -halfDepth);

            Vector3 frontTL = new Vector3(tl.X, tl.Y, +halfDepth);
            Vector3 frontTR = new Vector3(tr.X, tr.Y, +halfDepth);
            Vector3 frontBL = new Vector3(bl.X, bl.Y, +halfDepth);
            Vector3 frontBR = new Vector3(br.X, br.Y, +halfDepth);

            Rectangle sideSrc = src;
            Rectangle topSrc = src;
            Rectangle bottomSrc = src;

            switch (angledMode)
            {
                case 6: // center band
                    sideSrc = GetCenterBand(src);
                    topSrc = GetCenterBand(src);
                    bottomSrc = GetCenterBand(src);
                    break;

                case 7: // surface strips
                    sideSrc = GetCenterBand(src);
                    topSrc = GetSurfaceStrip(src, floor: false);
                    bottomSrc = GetSurfaceStrip(src, floor: true);
                    break;

                case 8: // edge
                    sideSrc = GetEdgeColumn(src, left: true);
                    topSrc = GetEdgeRow(src, top: true);
                    bottomSrc = GetEdgeRow(src, top: false);
                    break;

                case 9: // center col/row
                    sideSrc = GetCenterColumn(src);
                    topSrc = GetCenterRow(src);
                    bottomSrc = GetCenterRow(src);
                    break;

                case 10: // alpha/edge
                    sideSrc = GetEdgeColumn(src, left: true);
                    topSrc = GetEdgeRow(src, top: true);
                    bottomSrc = GetEdgeRow(src, top: false);
                    break;

                case 11: // hard stretch
                    sideSrc = GetCenterColumn(src);
                    topSrc = GetEdgeRow(src, top: true);
                    bottomSrc = GetEdgeRow(src, top: false);
                    break;
            }

            float texW = tex.Width;
            float texH = tex.Height;
            float uInset = 0.5f / texW;
            float vInset = 0.5f / texH;

            void GetUv(Rectangle r, out float u0, out float v0, out float u1, out float v1)
            {
                u0 = r.X / texW + uInset;
                v0 = r.Y / texH + vInset;
                u1 = (r.X + r.Width) / texW - uInset;
                v1 = (r.Y + r.Height) / texH - vInset;
            }

            GetUv(sideSrc, out float su0, out float sv0, out float su1, out float sv1);
            GetUv(topSrc, out float tu0, out float tv0, out float tu1, out float tv1);
            GetUv(bottomSrc, out float bu0, out float bv0, out float bu1, out float bv1);

            Color cSide = Mult(st.SpriteColor, angledMode == 11 ? 0.86f : 0.92f);
            Color cTop = Mult(st.SpriteColor, angledMode == 11 ? 0.92f : 0.98f);
            Color cBottom = Mult(st.SpriteColor, angledMode == 11 ? 0.70f : 0.80f);

            SetupBasicEffect3D();
            gd.DepthStencilState = DepthStencilState.Default;
            gd.BlendState = BlendState.NonPremultiplied;
            gd.RasterizerState = RasterizerState.CullNone;
            gd.SamplerStates[0] = SamplerState.PointClamp;

            // mode 5 = caps only
            if (angledMode == 5) return;

            // Левая боковина (bridge)
            DrawTexturedFace(gd, tex, backTL, frontTL, backBL, frontBL, su0, sv0, su1, sv1, cSide);

            // Правая боковина (bridge)
            DrawTexturedFace(gd, tex, frontTR, backTR, frontBR, backBR, su0, sv0, su1, sv1, cSide);

            // mode 4 = sides only
            if (angledMode == 4) return;

            // Верхняя bridge-грань
            DrawTexturedFace(gd, tex, backTL, backTR, frontTL, frontTR, tu0, tv0, tu1, tv1, cTop);

            // Нижняя bridge-грань
            DrawTexturedFace(gd, tex, frontBL, frontBR, backBL, backBR, bu0, bv0, bu1, bv1, cBottom);
        }
		
		
		
		
        private static float GetSelectedThickness()
        {
            return (limbDisplayMode, selectedSubPart) switch
            {
                (1, 0) => headThickness,
                (2, 0) => torsoThickness,
                (2, 1) => waistThickness,
                (3, 0) => leftArmThickness,
                (3, 1) => leftForearmThickness,
                (3, 2) => leftHandThickness,
                (4, 0) => rightArmThickness,
                (4, 1) => rightForearmThickness,
                (4, 2) => rightHandThickness,
                (5, 0) => leftThighThickness,
                (5, 1) => leftLegThickness,
                (5, 2) => leftFootThickness,
                (6, 0) => rightThighThickness,
                (6, 1) => rightLegThickness,
                (6, 2) => rightFootThickness,
                _ => 0f
            };
        }
		
        // ==================== ВАРИАНТ 1: Базовый скан всех пикселей ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V1(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = sourceRect.Width, maxX = 0, minY = sourceRect.Height, maxY = 0;
        
            try
            {
                Color[] allPixels = new Color[texture.Width * texture.Height];
                texture.GetData(allPixels);
        
                for (int y = 0; y < sourceRect.Height; y++)
                {
                    for (int x = 0; x < sourceRect.Width; x++)
                    {
                        int idx = (sourceRect.Y + y) * texture.Width + (sourceRect.X + x);
                        if (idx >= 0 && idx < allPixels.Length && allPixels[idx].A > 10)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }
            catch { }
        
            if (minX > maxX) { minX = 0; maxX = sourceRect.Width - 1; }
            if (minY > maxY) { minY = 0; maxY = sourceRect.Height - 1; }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 2: GetData только для региона ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V2(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = sourceRect.Width, maxX = -1, minY = sourceRect.Height, maxY = -1;
        
            try
            {
                Color[] regionPixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, regionPixels, 0, regionPixels.Length);
        
                for (int i = 0; i < regionPixels.Length; i++)
                {
                    if (regionPixels[i].A > 10)
                    {
                        int x = i % sourceRect.Width;
                        int y = i / sourceRect.Width;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            catch { }
        
            if (minX > maxX) { minX = 0; maxX = sourceRect.Width - 1; }
            if (minY > maxY) { minY = 0; maxY = sourceRect.Height - 1; }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 3: Скан с краёв к центру ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V3(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = 0, maxX = sourceRect.Width - 1;
            int minY = 0, maxY = sourceRect.Height - 1;
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
        
                // Сверху вниз
                bool found = false;
                for (int y = 0; y < sourceRect.Height && !found; y++)
                    for (int x = 0; x < w; x++)
                        if (pixels[y * w + x].A > 10) { minY = y; found = true; break; }
        
                // Снизу вверх
                found = false;
                for (int y = sourceRect.Height - 1; y >= 0 && !found; y--)
                    for (int x = 0; x < w; x++)
                        if (pixels[y * w + x].A > 10) { maxY = y; found = true; break; }
        
                // Слева направо
                found = false;
                for (int x = 0; x < w && !found; x++)
                    for (int y = minY; y <= maxY; y++)
                        if (pixels[y * w + x].A > 10) { minX = x; found = true; break; }
        
                // Справа налево
                found = false;
                for (int x = w - 1; x >= 0 && !found; x--)
                    for (int y = minY; y <= maxY; y++)
                        if (pixels[y * w + x].A > 10) { maxX = x; found = true; break; }
            }
            catch { }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 4: Фиксированный отступ 10% ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V4(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int marginX = Math.Max(1, sourceRect.Width / 10);
            int marginY = Math.Max(1, sourceRect.Height / 10);
        
            var result = (marginX, sourceRect.Width - 1 - marginX, marginY, sourceRect.Height - 1 - marginY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 5: Фиксированный отступ 5% ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V5(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int marginX = Math.Max(1, sourceRect.Width / 20);
            int marginY = Math.Max(1, sourceRect.Height / 20);
        
            var result = (marginX, sourceRect.Width - 1 - marginX, marginY, sourceRect.Height - 1 - marginY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 6: Порог альфы 50 ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V6(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = sourceRect.Width, maxX = -1, minY = sourceRect.Height, maxY = -1;
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
        
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].A >= 50)
                    {
                        int x = i % w;
                        int y = i / w;
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
            catch { }
        
            if (minX > maxX) { minX = 0; maxX = sourceRect.Width - 1; }
            if (minY > maxY) { minY = 0; maxY = sourceRect.Height - 1; }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 7: Порог альфы 100 ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V7(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = sourceRect.Width, maxX = -1, minY = sourceRect.Height, maxY = -1;
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
        
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].A >= 100)
                    {
                        int x = i % w;
                        int y = i / w;
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
            catch { }
        
            if (minX > maxX) { minX = 0; maxX = sourceRect.Width - 1; }
            if (minY > maxY) { minY = 0; maxY = sourceRect.Height - 1; }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 8: Порог альфы 1 (любой непрозрачный) ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V8(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = sourceRect.Width, maxX = -1, minY = sourceRect.Height, maxY = -1;
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
        
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].A > 0)
                    {
                        int x = i % w;
                        int y = i / w;
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
            catch { }
        
            if (minX > maxX) { minX = 0; maxX = sourceRect.Width - 1; }
            if (minY > maxY) { minY = 0; maxY = sourceRect.Height - 1; }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 9: Сэмплирование каждого 4-го пикселя ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V9(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = sourceRect.Width, maxX = -1, minY = sourceRect.Height, maxY = -1;
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
                int h = sourceRect.Height;
        
                for (int y = 0; y < h; y += 2)
                {
                    for (int x = 0; x < w; x += 2)
                    {
                        if (pixels[y * w + x].A > 10)
                        {
                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }
            }
            catch { }
        
            if (minX > maxX) { minX = 0; maxX = sourceRect.Width - 1; }
            if (minY > maxY) { minY = 0; maxY = sourceRect.Height - 1; }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 10: Проверка RGB + Alpha ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V10(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = sourceRect.Width, maxX = -1, minY = sourceRect.Height, maxY = -1;
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
        
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color c = pixels[i];
                    // Пиксель видимый если альфа > 10 И хотя бы один цветовой канал > 0
                    if (c.A > 10 && (c.R > 0 || c.G > 0 || c.B > 0))
                    {
                        int x = i % w;
                        int y = i / w;
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }
            catch { }
        
            if (minX > maxX) { minX = 0; maxX = sourceRect.Width - 1; }
            if (minY > maxY) { minY = 0; maxY = sourceRect.Height - 1; }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ВАРИАНТ 11: Бинарный поиск границ ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V11(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
                int h = sourceRect.Height;
        
                bool HasPixelInRow(int y)
                {
                    for (int x = 0; x < w; x++)
                        if (pixels[y * w + x].A > 10) return true;
                    return false;
                }
        
                bool HasPixelInCol(int x, int startY, int endY)
                {
                    for (int y = startY; y <= endY; y++)
                        if (pixels[y * w + x].A > 10) return true;
                    return false;
                }
        
                // Бинарный поиск minY
                int lo = 0, hi = h - 1, minY = 0;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    bool found = false;
                    for (int y = lo; y <= mid; y++)
                        if (HasPixelInRow(y)) { minY = y; found = true; break; }
                    if (found) hi = mid - 1;
                    else lo = mid + 1;
                }
        
                // Бинарный поиск maxY
                lo = minY; hi = h - 1; int maxY = h - 1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    bool found = false;
                    for (int y = hi; y >= mid; y--)
                        if (HasPixelInRow(y)) { maxY = y; found = true; break; }
                    if (found) lo = mid + 1;
                    else hi = mid - 1;
                }
        
                // Линейный поиск minX, maxX
                int minX = 0, maxX = w - 1;
                for (int x = 0; x < w; x++)
                    if (HasPixelInCol(x, minY, maxY)) { minX = x; break; }
                for (int x = w - 1; x >= 0; x--)
                    if (HasPixelInCol(x, minY, maxY)) { maxX = x; break; }
        
                var result = (minX, maxX, minY, maxY);
                textureBoundsCache[texture][sourceRect] = result;
                return result;
            }
            catch
            {
                var fallback = (0, sourceRect.Width - 1, 0, sourceRect.Height - 1);
                textureBoundsCache[texture][sourceRect] = fallback;
                return fallback;
            }
        }
        
        // ==================== ВАРИАНТ 12: Центр + радиус ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V12(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
                int h = sourceRect.Height;
        
                int centerX = w / 2;
                int centerY = h / 2;
                int maxDist = 0;
        
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].A > 10)
                    {
                        int x = i % w;
                        int y = i / w;
                        int dist = Math.Max(Math.Abs(x - centerX), Math.Abs(y - centerY));
                        maxDist = Math.Max(maxDist, dist);
                    }
                }
        
                int minX = Math.Max(0, centerX - maxDist);
                int maxX = Math.Min(w - 1, centerX + maxDist);
                int minY = Math.Max(0, centerY - maxDist);
                int maxY = Math.Min(h - 1, centerY + maxDist);
        
                var result = (minX, maxX, minY, maxY);
                textureBoundsCache[texture][sourceRect] = result;
                return result;
            }
            catch
            {
                var fallback = (0, sourceRect.Width - 1, 0, sourceRect.Height - 1);
                textureBoundsCache[texture][sourceRect] = fallback;
                return fallback;
            }
        }
        
        // ==================== ВАРИАНТ 13: Квантильный метод ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V13(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
        
                List<int> xCoords = new List<int>();
                List<int> yCoords = new List<int>();
        
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].A > 10)
                    {
                        xCoords.Add(i % w);
                        yCoords.Add(i / w);
                    }
                }
        
                if (xCoords.Count == 0)
                {
                    var fallback = (0, sourceRect.Width - 1, 0, sourceRect.Height - 1);
                    textureBoundsCache[texture][sourceRect] = fallback;
                    return fallback;
                }
        
                xCoords.Sort();
                yCoords.Sort();
        
                // Берём 1% и 99% квантили
                int idx1 = Math.Max(0, (int)(xCoords.Count * 0.01));
                int idx99 = Math.Min(xCoords.Count - 1, (int)(xCoords.Count * 0.99));
        
                int minX = xCoords[idx1];
                int maxX = xCoords[idx99];
                int minY = yCoords[idx1];
                int maxY = yCoords[idx99];
        
                var result = (minX, maxX, minY, maxY);
                textureBoundsCache[texture][sourceRect] = result;
                return result;
            }
            catch
            {
                var fallback = (0, sourceRect.Width - 1, 0, sourceRect.Height - 1);
                textureBoundsCache[texture][sourceRect] = fallback;
                return fallback;
            }
        }
        
        // ==================== ВАРИАНТ 14: Усреднённый bbox ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V14(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
        
                long sumX = 0, sumY = 0;
                int count = 0;
                int minX = sourceRect.Width, maxX = -1, minY = sourceRect.Height, maxY = -1;
        
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].A > 10)
                    {
                        int x = i % w;
                        int y = i / w;
                        sumX += x;
                        sumY += y;
                        count++;
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                    }
                }
        
                if (count == 0)
                {
                    var fallback = (0, sourceRect.Width - 1, 0, sourceRect.Height - 1);
                    textureBoundsCache[texture][sourceRect] = fallback;
                    return fallback;
                }
        
                // Среднее между реальными границами и центром масс
                int avgX = (int)(sumX / count);
                int avgY = (int)(sumY / count);
                
                int halfW = Math.Max(avgX - minX, maxX - avgX);
                int halfH = Math.Max(avgY - minY, maxY - avgY);
        
                minX = Math.Max(0, avgX - halfW);
                maxX = Math.Min(sourceRect.Width - 1, avgX + halfW);
                minY = Math.Max(0, avgY - halfH);
                maxY = Math.Min(sourceRect.Height - 1, avgY + halfH);
        
                var result = (minX, maxX, minY, maxY);
                textureBoundsCache[texture][sourceRect] = result;
                return result;
            }
            catch
            {
                var fallback = (0, sourceRect.Width - 1, 0, sourceRect.Height - 1);
                textureBoundsCache[texture][sourceRect] = fallback;
                return fallback;
            }
        }
        
        // ==================== ВАРИАНТ 15: Точные границы с отступом ====================
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds_V15(Texture2D texture, Rectangle sourceRect)
        {
            if (textureBoundsCache.TryGetValue(texture, out var rectCache))
                if (rectCache.TryGetValue(sourceRect, out var cached))
                    return cached;
            
            if (!textureBoundsCache.ContainsKey(texture))
                textureBoundsCache[texture] = new Dictionary<Rectangle, (int, int, int, int)>();
        
            int minX = sourceRect.Width, maxX = -1, minY = sourceRect.Height, maxY = -1;
        
            try
            {
                Color[] pixels = new Color[sourceRect.Width * sourceRect.Height];
                texture.GetData(0, sourceRect, pixels, 0, pixels.Length);
                int w = sourceRect.Width;
                int h = sourceRect.Height;
        
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (pixels[y * w + x].A > 5)
                        {
                            minX = Math.Min(minX, x);
                            maxX = Math.Max(maxX, x);
                            minY = Math.Min(minY, y);
                            maxY = Math.Max(maxY, y);
                        }
                    }
                }
        
                // Добавляем 2 пикселя отступа
                minX = Math.Max(0, minX - 2);
                maxX = Math.Min(w - 1, maxX + 2);
                minY = Math.Max(0, minY - 2);
                maxY = Math.Min(h - 1, maxY + 2);
            }
            catch { }
        
            if (minX > maxX) { minX = 0; maxX = sourceRect.Width - 1; }
            if (minY > maxY) { minY = 0; maxY = sourceRect.Height - 1; }
        
            var result = (minX, maxX, minY, maxY);
            textureBoundsCache[texture][sourceRect] = result;
            return result;
        }
        
        // ==================== ГЛАВНАЯ ФУНКЦИЯ - ВЫБЕРИ НОМЕР ВАРИАНТА ====================
        private static int textureBoundsVariant = 2; // Меняй это число от 1 до 15
        
        private static (int minX, int maxX, int minY, int maxY) GetTextureBounds(Texture2D texture, Rectangle sourceRect)
        {
            return textureBoundsVariant switch
            {
                1 => GetTextureBounds_V1(texture, sourceRect),
                2 => GetTextureBounds_V2(texture, sourceRect),
                3 => GetTextureBounds_V3(texture, sourceRect),
                4 => GetTextureBounds_V4(texture, sourceRect),
                5 => GetTextureBounds_V5(texture, sourceRect),
                6 => GetTextureBounds_V6(texture, sourceRect),
                7 => GetTextureBounds_V7(texture, sourceRect),
                8 => GetTextureBounds_V8(texture, sourceRect),
                9 => GetTextureBounds_V9(texture, sourceRect),
                10 => GetTextureBounds_V10(texture, sourceRect),
                11 => GetTextureBounds_V11(texture, sourceRect),
                12 => GetTextureBounds_V12(texture, sourceRect),
                13 => GetTextureBounds_V13(texture, sourceRect),
                14 => GetTextureBounds_V14(texture, sourceRect),
                15 => GetTextureBounds_V15(texture, sourceRect),
                _ => GetTextureBounds_V2(texture, sourceRect)
            };
        }

        
        private static int CountOpaquePixels(Texture2D tex, Rectangle srcRect, byte alphaThreshold)
        {
            try
            {
                if (!TryGetSpriteRegionPixels(tex, srcRect, out var pixels) || pixels == null) return -1;

                int count = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].A >= alphaThreshold) count++;
                }
                return count;
            }
            catch
            {
                return -1;
            }
        }

        private static void DebugNearestNpcExtrusion()
        {
            Vector2 camPos = GetCameraPosition();
            Character nearest = null;
            float minDist = float.MaxValue;

            foreach (var c in Character.CharacterList)
            {
                if (c == Character.Controlled) continue;
                if (c?.AnimController?.Limbs == null) continue;

                float dist = Vector2.Distance(camPos, c.WorldPosition);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = c;
                }
            }

            if (nearest?.AnimController?.Limbs == null)
            {
                DebugConsole.NewMessage("ExtrusionDebug: nearest NPC not found", Color.Red);
                return;
            }

            DebugConsole.NewMessage($"=== Extrusion Debug: {nearest.SpeciesName} ({minDist:F0}px) ===", Color.Yellow);

            byte[] thresholds = { 1, 8, 16, 24, 32 };

            for (int i = 0; i < nearest.AnimController.Limbs.Length; i++)
            {
                var limb = nearest.AnimController.Limbs[i];
                if (limb == null) continue;

                Sprite sprite = GetLimbSprite(limb);
                if (sprite?.Texture == null || sprite.Texture.IsDisposed)
                {
                    DebugConsole.NewMessage($"[{i}] {limb.type}: sprite/texture NULL", Color.Orange);
                    continue;
                }

                Rectangle src = sprite.SourceRect;
                if (src.Width <= 0 || src.Height <= 0)
                {
                    DebugConsole.NewMessage($"[{i}] {limb.type}: bad srcRect {src.Width}x{src.Height}", Color.Orange);
                    continue;
                }

                DebugConsole.NewMessage(
                    $"[{i}] {limb.type} src={src.Width}x{src.Height} tex={sprite.Texture.Width}x{sprite.Texture.Height}",
                    Color.White);

                foreach (byte th in thresholds)
                {
                    int opaque = CountOpaquePixels(sprite.Texture, src, th);
                    var mesh = GetOrBuildExtrudedMesh(sprite.Texture, src, th);

                    int sideVerts = mesh?.SideVertices?.Length ?? 0;
                    int sideTris = (mesh?.SideIndices?.Length ?? 0) / 3;

                    Color lineColor = sideTris > 0 ? Color.Lime : Color.Orange;
                    DebugConsole.NewMessage(
                        $"    th={th,2} opaque={opaque,5} sideVerts={sideVerts,6} sideTris={sideTris,6}",
                        lineColor);
                }
            }

            DebugConsole.NewMessage("=== End Extrusion Debug ===", Color.Yellow);
        }
        private static bool TryGetSpriteRegionPixels(Texture2D tex, Rectangle srcRect, out Color[] pixels)
        {
            pixels = null;
            if (tex == null || tex.IsDisposed || srcRect.Width <= 0 || srcRect.Height <= 0) return false;

            var key = (tex, srcRect);
            if (spriteRegionPixelCache.TryGetValue(key, out var cached))
            {
                pixels = cached;
                return true;
            }

            try
            {
                EnsureResources();
                var gd = GameMain.Instance.GraphicsDevice;

                var oldTargets = gd.GetRenderTargets();
                using var rt = new RenderTarget2D(
                    gd,
                    srcRect.Width,
                    srcRect.Height,
                    false,
                    SurfaceFormat.Color,
                    DepthFormat.None);

                gd.SetRenderTarget(rt);
                gd.Clear(Color.Transparent);

                fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, null, RasterizerState.CullNone);
                fpBatch.Draw(tex, new Rectangle(0, 0, srcRect.Width, srcRect.Height), srcRect, Color.White);
                fpBatch.End();

                gd.SetRenderTargets(oldTargets);

                pixels = new Color[srcRect.Width * srcRect.Height];
                rt.GetData(pixels);

                spriteRegionPixelCache[key] = pixels;
                return true;
            }
            catch
            {
                pixels = null;
                return false;
            }
        }
		
        // Кэш границ текстур (чтобы не анализировать каждый кадр)
        private static Dictionary<Texture2D, Dictionary<Rectangle, (int minX, int maxX, int minY, int maxY)>> textureBoundsCache 
            = new Dictionary<Texture2D, Dictionary<Rectangle, (int, int, int, int)>>();
        
        // Режим фона
        private static int limbBackgroundMode = 0;
        private const int LIMB_BG_MODE_COUNT = 4;
        private static void SetSelectedThickness(float value)
        {
            if (value < 0) value = 0;
            switch (limbDisplayMode, selectedSubPart)
            {
                case (1, 0): headThickness = value; break;
                case (2, 0): torsoThickness = value; break;
                case (2, 1): waistThickness = value; break;
                case (3, 0): leftArmThickness = value; break;
                case (3, 1): leftForearmThickness = value; break;
                case (3, 2): leftHandThickness = value; break;
                case (4, 0): rightArmThickness = value; break;
                case (4, 1): rightForearmThickness = value; break;
                case (4, 2): rightHandThickness = value; break;
                case (5, 0): leftThighThickness = value; break;
                case (5, 1): leftLegThickness = value; break;
                case (5, 2): leftFootThickness = value; break;
                case (6, 0): rightThighThickness = value; break;
                case (6, 1): rightLegThickness = value; break;
                case (6, 2): rightFootThickness = value; break;
            }
        }
        private static float GetArmExtraRotRad(LimbType t)
        {
            float deg = t switch
            {
                LimbType.LeftArm => leftArmRotDeg,
                LimbType.LeftForearm => leftForearmRotDeg,
                LimbType.LeftHand => leftHandRotDeg,

                LimbType.RightArm => rightArmRotDeg,
                LimbType.RightForearm => rightForearmRotDeg,
                LimbType.RightHand => rightHandRotDeg,

                _ => 0f
            };

            return MathHelper.ToRadians(deg);
        }
        private static string GetCharacterModeName(int mode) => mode switch
        {
            0 => "2D (default)",
            1 => "Balanced",
            2 => "Wide arms",
            3 => "Thick center",
            4 => "Slim",
            5 => "Chunky",
            6 => "Even",
            7 => "Compact",
            8 => "Medium+",
            9 => "Minimal",
            10 => "Maximum",
            _ => $"Mode {mode}"
        };

        // ==================== РАЗМЕРЫ ГРАНЕЙ ДЛЯ КАЖДОЙ ЧАСТИ ====================
        // Scale = множитель размера грани (1.0 = нормальный размер)
        // ==========================================================================
        
        // ГОЛОВА - размеры граней
        private static float headFrontScale = 1f;
        private static float headBackScale = 1f;
        private static float headTopScale = 1f;
        private static float headBottomScale = 1f;
        private static float headLeftScale = 1f;
        private static float headRightScale = 1f;
        
        // ТУЛОВИЩЕ - размеры граней
        private static float torsoFrontScale = 1f;
        private static float torsoBackScale = 1f;
        private static float torsoTopScale = 1f;
        private static float torsoBottomScale = 1f;
        private static float torsoLeftScale = 1f;
        private static float torsoRightScale = 1f;
        
        // ТАЛИЯ - размеры граней
        private static float waistFrontScale = 1f;
        private static float waistBackScale = 1f;
        private static float waistTopScale = 1f;
        private static float waistBottomScale = 1f;
        private static float waistLeftScale = 1f;
        private static float waistRightScale = 1f;
        
        // ЛЕВОЕ ПЛЕЧО - размеры граней
        private static float leftArmFrontScale = 1f;
        private static float leftArmBackScale = 1f;
        private static float leftArmTopScale = 1f;
        private static float leftArmBottomScale = 1f;
        private static float leftArmLeftScale = 1f;
        private static float leftArmRightScale = 1f;
        
        // ПРАВОЕ ПЛЕЧО - размеры граней
        private static float rightArmFrontScale = 1f;
        private static float rightArmBackScale = 1f;
        private static float rightArmTopScale = 1f;
        private static float rightArmBottomScale = 1f;
        private static float rightArmLeftScale = 1f;
        private static float rightArmRightScale = 1f;
        
        // ЛЕВОЕ ПРЕДПЛЕЧЬЕ - размеры граней
        private static float leftForearmFrontScale = 1f;
        private static float leftForearmBackScale = 1f;
        private static float leftForearmTopScale = 1f;
        private static float leftForearmBottomScale = 1f;
        private static float leftForearmLeftScale = 1f;
        private static float leftForearmRightScale = 1f;
        
        // ПРАВОЕ ПРЕДПЛЕЧЬЕ - размеры граней
        private static float rightForearmFrontScale = 1f;
        private static float rightForearmBackScale = 1f;
        private static float rightForearmTopScale = 1f;
        private static float rightForearmBottomScale = 1f;
        private static float rightForearmLeftScale = 1f;
        private static float rightForearmRightScale = 1f;
        
        // ЛЕВАЯ КИСТЬ - размеры граней
        private static float leftHandFrontScale = 1f;
        private static float leftHandBackScale = 1f;
        private static float leftHandTopScale = 1f;
        private static float leftHandBottomScale = 1f;
        private static float leftHandLeftScale = 1f;
        private static float leftHandRightScale = 1f;
        
        // ПРАВАЯ КИСТЬ - размеры граней
        private static float rightHandFrontScale = 1f;
        private static float rightHandBackScale = 1f;
        private static float rightHandTopScale = 1f;
        private static float rightHandBottomScale = 1f;
        private static float rightHandLeftScale = 1f;
        private static float rightHandRightScale = 1f;
        
        // ЛЕВОЕ БЕДРО - размеры граней
        private static float leftThighFrontScale = 1f;
        private static float leftThighBackScale = 1f;
        private static float leftThighTopScale = 1f;
        private static float leftThighBottomScale = 1f;
        private static float leftThighLeftScale = 1f;
        private static float leftThighRightScale = 1f;
        
        // ПРАВОЕ БЕДРО - размеры граней
        private static float rightThighFrontScale = 1f;
        private static float rightThighBackScale = 1f;
        private static float rightThighTopScale = 1f;
        private static float rightThighBottomScale = 1f;
        private static float rightThighLeftScale = 1f;
        private static float rightThighRightScale = 1f;
        
        // ЛЕВАЯ ГОЛЕНЬ - размеры граней
        private static float leftLegFrontScale = 1f;
        private static float leftLegBackScale = 1f;
        private static float leftLegTopScale = 1f;
        private static float leftLegBottomScale = 1f;
        private static float leftLegLeftScale = 1f;
        private static float leftLegRightScale = 1f;
        
        // ПРАВАЯ ГОЛЕНЬ - размеры граней
        private static float rightLegFrontScale = 1f;
        private static float rightLegBackScale = 1f;
        private static float rightLegTopScale = 1f;
        private static float rightLegBottomScale = 1f;
        private static float rightLegLeftScale = 1f;
        private static float rightLegRightScale = 1f;
        
        // ЛЕВАЯ СТОПА - размеры граней
        private static float leftFootFrontScale = 1f;
        private static float leftFootBackScale = 1f;
        private static float leftFootTopScale = 1f;
        private static float leftFootBottomScale = 1f;
        private static float leftFootLeftScale = 1f;
        private static float leftFootRightScale = 1f;
        
        // ПРАВАЯ СТОПА - размеры граней
        private static float rightFootFrontScale = 1f;
        private static float rightFootBackScale = 1f;
        private static float rightFootTopScale = 1f;
        private static float rightFootBottomScale = 1f;
        private static float rightFootLeftScale = 1f;
        private static float rightFootRightScale = 1f;
        
        // Режим рендера персонажей (0 = нормальный с глубиной, 1 = видеть сквозь всё)

        private static (float scaleX, float scaleY) GetSelectedFaceScale()
        {
            return (limbDisplayMode, selectedSubPart, selectedFace) switch
            {
                // HEAD
                (1, 0, 0) => (headFrontScaleX, headFrontScaleY),
                (1, 0, 1) => (headBackScaleX, headBackScaleY),
                (1, 0, 2) => (headTopScaleX, headTopScaleY),
                (1, 0, 3) => (headBottomScaleX, headBottomScaleY),
                (1, 0, 4) => (headLeftScaleX, headLeftScaleY),
                (1, 0, 5) => (headRightScaleX, headRightScaleY),
        
                // TORSO
                (2, 0, 0) => (torsoFrontScaleX, torsoFrontScaleY),
                (2, 0, 1) => (torsoBackScaleX, torsoBackScaleY),
                (2, 0, 2) => (torsoTopScaleX, torsoTopScaleY),
                (2, 0, 3) => (torsoBottomScaleX, torsoBottomScaleY),
                (2, 0, 4) => (torsoLeftScaleX, torsoLeftScaleY),
                (2, 0, 5) => (torsoRightScaleX, torsoRightScaleY),
        
                // WAIST
                (2, 1, 0) => (waistFrontScaleX, waistFrontScaleY),
                (2, 1, 1) => (waistBackScaleX, waistBackScaleY),
                (2, 1, 2) => (waistTopScaleX, waistTopScaleY),
                (2, 1, 3) => (waistBottomScaleX, waistBottomScaleY),
                (2, 1, 4) => (waistLeftScaleX, waistLeftScaleY),
                (2, 1, 5) => (waistRightScaleX, waistRightScaleY),
        
                // LEFT ARM
                (3, 0, 0) => (leftArmFrontScaleX, leftArmFrontScaleY),
                (3, 0, 1) => (leftArmBackScaleX, leftArmBackScaleY),
                (3, 0, 2) => (leftArmTopScaleX, leftArmTopScaleY),
                (3, 0, 3) => (leftArmBottomScaleX, leftArmBottomScaleY),
                (3, 0, 4) => (leftArmLeftScaleX, leftArmLeftScaleY),
                (3, 0, 5) => (leftArmRightScaleX, leftArmRightScaleY),
        
                // LEFT FOREARM
                (3, 1, 0) => (leftForearmFrontScaleX, leftForearmFrontScaleY),
                (3, 1, 1) => (leftForearmBackScaleX, leftForearmBackScaleY),
                (3, 1, 2) => (leftForearmTopScaleX, leftForearmTopScaleY),
                (3, 1, 3) => (leftForearmBottomScaleX, leftForearmBottomScaleY),
                (3, 1, 4) => (leftForearmLeftScaleX, leftForearmLeftScaleY),
                (3, 1, 5) => (leftForearmRightScaleX, leftForearmRightScaleY),
        
                // LEFT HAND
                (3, 2, 0) => (leftHandFrontScaleX, leftHandFrontScaleY),
                (3, 2, 1) => (leftHandBackScaleX, leftHandBackScaleY),
                (3, 2, 2) => (leftHandTopScaleX, leftHandTopScaleY),
                (3, 2, 3) => (leftHandBottomScaleX, leftHandBottomScaleY),
                (3, 2, 4) => (leftHandLeftScaleX, leftHandLeftScaleY),
                (3, 2, 5) => (leftHandRightScaleX, leftHandRightScaleY),
        
                // RIGHT ARM
                (4, 0, 0) => (rightArmFrontScaleX, rightArmFrontScaleY),
                (4, 0, 1) => (rightArmBackScaleX, rightArmBackScaleY),
                (4, 0, 2) => (rightArmTopScaleX, rightArmTopScaleY),
                (4, 0, 3) => (rightArmBottomScaleX, rightArmBottomScaleY),
                (4, 0, 4) => (rightArmLeftScaleX, rightArmLeftScaleY),
                (4, 0, 5) => (rightArmRightScaleX, rightArmRightScaleY),
        
                // RIGHT FOREARM
                (4, 1, 0) => (rightForearmFrontScaleX, rightForearmFrontScaleY),
                (4, 1, 1) => (rightForearmBackScaleX, rightForearmBackScaleY),
                (4, 1, 2) => (rightForearmTopScaleX, rightForearmTopScaleY),
                (4, 1, 3) => (rightForearmBottomScaleX, rightForearmBottomScaleY),
                (4, 1, 4) => (rightForearmLeftScaleX, rightForearmLeftScaleY),
                (4, 1, 5) => (rightForearmRightScaleX, rightForearmRightScaleY),
        
                // RIGHT HAND
                (4, 2, 0) => (rightHandFrontScaleX, rightHandFrontScaleY),
                (4, 2, 1) => (rightHandBackScaleX, rightHandBackScaleY),
                (4, 2, 2) => (rightHandTopScaleX, rightHandTopScaleY),
                (4, 2, 3) => (rightHandBottomScaleX, rightHandBottomScaleY),
                (4, 2, 4) => (rightHandLeftScaleX, rightHandLeftScaleY),
                (4, 2, 5) => (rightHandRightScaleX, rightHandRightScaleY),
        
                // LEFT THIGH
                (5, 0, 0) => (leftThighFrontScaleX, leftThighFrontScaleY),
                (5, 0, 1) => (leftThighBackScaleX, leftThighBackScaleY),
                (5, 0, 2) => (leftThighTopScaleX, leftThighTopScaleY),
                (5, 0, 3) => (leftThighBottomScaleX, leftThighBottomScaleY),
                (5, 0, 4) => (leftThighLeftScaleX, leftThighLeftScaleY),
                (5, 0, 5) => (leftThighRightScaleX, leftThighRightScaleY),
        
                // LEFT LEG
                (5, 1, 0) => (leftLegFrontScaleX, leftLegFrontScaleY),
                (5, 1, 1) => (leftLegBackScaleX, leftLegBackScaleY),
                (5, 1, 2) => (leftLegTopScaleX, leftLegTopScaleY),
                (5, 1, 3) => (leftLegBottomScaleX, leftLegBottomScaleY),
                (5, 1, 4) => (leftLegLeftScaleX, leftLegLeftScaleY),
                (5, 1, 5) => (leftLegRightScaleX, leftLegRightScaleY),
        
                // LEFT FOOT
                (5, 2, 0) => (leftFootFrontScaleX, leftFootFrontScaleY),
                (5, 2, 1) => (leftFootBackScaleX, leftFootBackScaleY),
                (5, 2, 2) => (leftFootTopScaleX, leftFootTopScaleY),
                (5, 2, 3) => (leftFootBottomScaleX, leftFootBottomScaleY),
                (5, 2, 4) => (leftFootLeftScaleX, leftFootLeftScaleY),
                (5, 2, 5) => (leftFootRightScaleX, leftFootRightScaleY),
        
                // RIGHT THIGH
                (6, 0, 0) => (rightThighFrontScaleX, rightThighFrontScaleY),
                (6, 0, 1) => (rightThighBackScaleX, rightThighBackScaleY),
                (6, 0, 2) => (rightThighTopScaleX, rightThighTopScaleY),
                (6, 0, 3) => (rightThighBottomScaleX, rightThighBottomScaleY),
                (6, 0, 4) => (rightThighLeftScaleX, rightThighLeftScaleY),
                (6, 0, 5) => (rightThighRightScaleX, rightThighRightScaleY),
        
                // RIGHT LEG
                (6, 1, 0) => (rightLegFrontScaleX, rightLegFrontScaleY),
                (6, 1, 1) => (rightLegBackScaleX, rightLegBackScaleY),
                (6, 1, 2) => (rightLegTopScaleX, rightLegTopScaleY),
                (6, 1, 3) => (rightLegBottomScaleX, rightLegBottomScaleY),
                (6, 1, 4) => (rightLegLeftScaleX, rightLegLeftScaleY),
                (6, 1, 5) => (rightLegRightScaleX, rightLegRightScaleY),
        
                // RIGHT FOOT
                (6, 2, 0) => (rightFootFrontScaleX, rightFootFrontScaleY),
                (6, 2, 1) => (rightFootBackScaleX, rightFootBackScaleY),
                (6, 2, 2) => (rightFootTopScaleX, rightFootTopScaleY),
                (6, 2, 3) => (rightFootBottomScaleX, rightFootBottomScaleY),
                (6, 2, 4) => (rightFootLeftScaleX, rightFootLeftScaleY),
                (6, 2, 5) => (rightFootRightScaleX, rightFootRightScaleY),
        
                _ => (1f, 1f)
            };
        }
        
        private static void SetSelectedFaceScaleX(float value)
        {
            if (value < 0.1f) value = 0.1f;
            
            switch (limbDisplayMode, selectedSubPart, selectedFace)
            {
                case (1, 0, 0): headFrontScaleX = value; break;
                case (1, 0, 1): headBackScaleX = value; break;
                case (1, 0, 2): headTopScaleX = value; break;
                case (1, 0, 3): headBottomScaleX = value; break;
                case (1, 0, 4): headLeftScaleX = value; break;
                case (1, 0, 5): headRightScaleX = value; break;
        
                case (2, 0, 0): torsoFrontScaleX = value; break;
                case (2, 0, 1): torsoBackScaleX = value; break;
                case (2, 0, 2): torsoTopScaleX = value; break;
                case (2, 0, 3): torsoBottomScaleX = value; break;
                case (2, 0, 4): torsoLeftScaleX = value; break;
                case (2, 0, 5): torsoRightScaleX = value; break;
        
                case (2, 1, 0): waistFrontScaleX = value; break;
                case (2, 1, 1): waistBackScaleX = value; break;
                case (2, 1, 2): waistTopScaleX = value; break;
                case (2, 1, 3): waistBottomScaleX = value; break;
                case (2, 1, 4): waistLeftScaleX = value; break;
                case (2, 1, 5): waistRightScaleX = value; break;
        
                case (3, 0, 0): leftArmFrontScaleX = value; break;
                case (3, 0, 1): leftArmBackScaleX = value; break;
                case (3, 0, 2): leftArmTopScaleX = value; break;
                case (3, 0, 3): leftArmBottomScaleX = value; break;
                case (3, 0, 4): leftArmLeftScaleX = value; break;
                case (3, 0, 5): leftArmRightScaleX = value; break;
        
                case (3, 1, 0): leftForearmFrontScaleX = value; break;
                case (3, 1, 1): leftForearmBackScaleX = value; break;
                case (3, 1, 2): leftForearmTopScaleX = value; break;
                case (3, 1, 3): leftForearmBottomScaleX = value; break;
                case (3, 1, 4): leftForearmLeftScaleX = value; break;
                case (3, 1, 5): leftForearmRightScaleX = value; break;
        
                case (3, 2, 0): leftHandFrontScaleX = value; break;
                case (3, 2, 1): leftHandBackScaleX = value; break;
                case (3, 2, 2): leftHandTopScaleX = value; break;
                case (3, 2, 3): leftHandBottomScaleX = value; break;
                case (3, 2, 4): leftHandLeftScaleX = value; break;
                case (3, 2, 5): leftHandRightScaleX = value; break;
        
                case (4, 0, 0): rightArmFrontScaleX = value; break;
                case (4, 0, 1): rightArmBackScaleX = value; break;
                case (4, 0, 2): rightArmTopScaleX = value; break;
                case (4, 0, 3): rightArmBottomScaleX = value; break;
                case (4, 0, 4): rightArmLeftScaleX = value; break;
                case (4, 0, 5): rightArmRightScaleX = value; break;
        
                case (4, 1, 0): rightForearmFrontScaleX = value; break;
                case (4, 1, 1): rightForearmBackScaleX = value; break;
                case (4, 1, 2): rightForearmTopScaleX = value; break;
                case (4, 1, 3): rightForearmBottomScaleX = value; break;
                case (4, 1, 4): rightForearmLeftScaleX = value; break;
                case (4, 1, 5): rightForearmRightScaleX = value; break;
        
                case (4, 2, 0): rightHandFrontScaleX = value; break;
                case (4, 2, 1): rightHandBackScaleX = value; break;
                case (4, 2, 2): rightHandTopScaleX = value; break;
                case (4, 2, 3): rightHandBottomScaleX = value; break;
                case (4, 2, 4): rightHandLeftScaleX = value; break;
                case (4, 2, 5): rightHandRightScaleX = value; break;
        
                case (5, 0, 0): leftThighFrontScaleX = value; break;
                case (5, 0, 1): leftThighBackScaleX = value; break;
                case (5, 0, 2): leftThighTopScaleX = value; break;
                case (5, 0, 3): leftThighBottomScaleX = value; break;
                case (5, 0, 4): leftThighLeftScaleX = value; break;
                case (5, 0, 5): leftThighRightScaleX = value; break;
        
                case (5, 1, 0): leftLegFrontScaleX = value; break;
                case (5, 1, 1): leftLegBackScaleX = value; break;
                case (5, 1, 2): leftLegTopScaleX = value; break;
                case (5, 1, 3): leftLegBottomScaleX = value; break;
                case (5, 1, 4): leftLegLeftScaleX = value; break;
                case (5, 1, 5): leftLegRightScaleX = value; break;
        
                case (5, 2, 0): leftFootFrontScaleX = value; break;
                case (5, 2, 1): leftFootBackScaleX = value; break;
                case (5, 2, 2): leftFootTopScaleX = value; break;
                case (5, 2, 3): leftFootBottomScaleX = value; break;
                case (5, 2, 4): leftFootLeftScaleX = value; break;
                case (5, 2, 5): leftFootRightScaleX = value; break;
        
                case (6, 0, 0): rightThighFrontScaleX = value; break;
                case (6, 0, 1): rightThighBackScaleX = value; break;
                case (6, 0, 2): rightThighTopScaleX = value; break;
                case (6, 0, 3): rightThighBottomScaleX = value; break;
                case (6, 0, 4): rightThighLeftScaleX = value; break;
                case (6, 0, 5): rightThighRightScaleX = value; break;
        
                case (6, 1, 0): rightLegFrontScaleX = value; break;
                case (6, 1, 1): rightLegBackScaleX = value; break;
                case (6, 1, 2): rightLegTopScaleX = value; break;
                case (6, 1, 3): rightLegBottomScaleX = value; break;
                case (6, 1, 4): rightLegLeftScaleX = value; break;
                case (6, 1, 5): rightLegRightScaleX = value; break;
        
                case (6, 2, 0): rightFootFrontScaleX = value; break;
                case (6, 2, 1): rightFootBackScaleX = value; break;
                case (6, 2, 2): rightFootTopScaleX = value; break;
                case (6, 2, 3): rightFootBottomScaleX = value; break;
                case (6, 2, 4): rightFootLeftScaleX = value; break;
                case (6, 2, 5): rightFootRightScaleX = value; break;
            }
        }
        
        private static void SetSelectedFaceScaleY(float value)
        {
            if (value < 0.1f) value = 0.1f;
            
            switch (limbDisplayMode, selectedSubPart, selectedFace)
            {
                case (1, 0, 0): headFrontScaleY = value; break;
                case (1, 0, 1): headBackScaleY = value; break;
                case (1, 0, 2): headTopScaleY = value; break;
                case (1, 0, 3): headBottomScaleY = value; break;
                case (1, 0, 4): headLeftScaleY = value; break;
                case (1, 0, 5): headRightScaleY = value; break;
        
                case (2, 0, 0): torsoFrontScaleY = value; break;
                case (2, 0, 1): torsoBackScaleY = value; break;
                case (2, 0, 2): torsoTopScaleY = value; break;
                case (2, 0, 3): torsoBottomScaleY = value; break;
                case (2, 0, 4): torsoLeftScaleY = value; break;
                case (2, 0, 5): torsoRightScaleY = value; break;
        
                case (2, 1, 0): waistFrontScaleY = value; break;
                case (2, 1, 1): waistBackScaleY = value; break;
                case (2, 1, 2): waistTopScaleY = value; break;
                case (2, 1, 3): waistBottomScaleY = value; break;
                case (2, 1, 4): waistLeftScaleY = value; break;
                case (2, 1, 5): waistRightScaleY = value; break;
        
                case (3, 0, 0): leftArmFrontScaleY = value; break;
                case (3, 0, 1): leftArmBackScaleY = value; break;
                case (3, 0, 2): leftArmTopScaleY = value; break;
                case (3, 0, 3): leftArmBottomScaleY = value; break;
                case (3, 0, 4): leftArmLeftScaleY = value; break;
                case (3, 0, 5): leftArmRightScaleY = value; break;
        
                case (3, 1, 0): leftForearmFrontScaleY = value; break;
                case (3, 1, 1): leftForearmBackScaleY = value; break;
                case (3, 1, 2): leftForearmTopScaleY = value; break;
                case (3, 1, 3): leftForearmBottomScaleY = value; break;
                case (3, 1, 4): leftForearmLeftScaleY = value; break;
                case (3, 1, 5): leftForearmRightScaleY = value; break;
        
                case (3, 2, 0): leftHandFrontScaleY = value; break;
                case (3, 2, 1): leftHandBackScaleY = value; break;
                case (3, 2, 2): leftHandTopScaleY = value; break;
                case (3, 2, 3): leftHandBottomScaleY = value; break;
                case (3, 2, 4): leftHandLeftScaleY = value; break;
                case (3, 2, 5): leftHandRightScaleY = value; break;
        
                case (4, 0, 0): rightArmFrontScaleY = value; break;
                case (4, 0, 1): rightArmBackScaleY = value; break;
                case (4, 0, 2): rightArmTopScaleY = value; break;
                case (4, 0, 3): rightArmBottomScaleY = value; break;
                case (4, 0, 4): rightArmLeftScaleY = value; break;
                case (4, 0, 5): rightArmRightScaleY = value; break;
        
                case (4, 1, 0): rightForearmFrontScaleY = value; break;
                case (4, 1, 1): rightForearmBackScaleY = value; break;
                case (4, 1, 2): rightForearmTopScaleY = value; break;
                case (4, 1, 3): rightForearmBottomScaleY = value; break;
                case (4, 1, 4): rightForearmLeftScaleY = value; break;
                case (4, 1, 5): rightForearmRightScaleY = value; break;
        
                case (4, 2, 0): rightHandFrontScaleY = value; break;
                case (4, 2, 1): rightHandBackScaleY = value; break;
                case (4, 2, 2): rightHandTopScaleY = value; break;
                case (4, 2, 3): rightHandBottomScaleY = value; break;
                case (4, 2, 4): rightHandLeftScaleY = value; break;
                case (4, 2, 5): rightHandRightScaleY = value; break;
        
                case (5, 0, 0): leftThighFrontScaleY = value; break;
                case (5, 0, 1): leftThighBackScaleY = value; break;
                case (5, 0, 2): leftThighTopScaleY = value; break;
                case (5, 0, 3): leftThighBottomScaleY = value; break;
                case (5, 0, 4): leftThighLeftScaleY = value; break;
                case (5, 0, 5): leftThighRightScaleY = value; break;
        
                case (5, 1, 0): leftLegFrontScaleY = value; break;
                case (5, 1, 1): leftLegBackScaleY = value; break;
                case (5, 1, 2): leftLegTopScaleY = value; break;
                case (5, 1, 3): leftLegBottomScaleY = value; break;
                case (5, 1, 4): leftLegLeftScaleY = value; break;
                case (5, 1, 5): leftLegRightScaleY = value; break;
        
                case (5, 2, 0): leftFootFrontScaleY = value; break;
                case (5, 2, 1): leftFootBackScaleY = value; break;
                case (5, 2, 2): leftFootTopScaleY = value; break;
                case (5, 2, 3): leftFootBottomScaleY = value; break;
                case (5, 2, 4): leftFootLeftScaleY = value; break;
                case (5, 2, 5): leftFootRightScaleY = value; break;
        
                case (6, 0, 0): rightThighFrontScaleY = value; break;
                case (6, 0, 1): rightThighBackScaleY = value; break;
                case (6, 0, 2): rightThighTopScaleY = value; break;
                case (6, 0, 3): rightThighBottomScaleY = value; break;
                case (6, 0, 4): rightThighLeftScaleY = value; break;
                case (6, 0, 5): rightThighRightScaleY = value; break;
        
                case (6, 1, 0): rightLegFrontScaleY = value; break;
                case (6, 1, 1): rightLegBackScaleY = value; break;
                case (6, 1, 2): rightLegTopScaleY = value; break;
                case (6, 1, 3): rightLegBottomScaleY = value; break;
                case (6, 1, 4): rightLegLeftScaleY = value; break;
                case (6, 1, 5): rightLegRightScaleY = value; break;
        
                case (6, 2, 0): rightFootFrontScaleY = value; break;
                case (6, 2, 1): rightFootBackScaleY = value; break;
                case (6, 2, 2): rightFootTopScaleY = value; break;
                case (6, 2, 3): rightFootBottomScaleY = value; break;
                case (6, 2, 4): rightFootLeftScaleY = value; break;
                case (6, 2, 5): rightFootRightScaleY = value; break;
            }
        }
        
        private static (float fScaleX, float fScaleY, float bScaleX, float bScaleY, 
                        float tScaleX, float tScaleY, float boScaleX, float boScaleY,
                        float lScaleX, float lScaleY, float rScaleX, float rScaleY) GetFaceScales(LimbType limbType)
        {
            return limbType switch
            {
                LimbType.Head => (headFrontScaleX, headFrontScaleY, headBackScaleX, headBackScaleY, 
                                  headTopScaleX, headTopScaleY, headBottomScaleX, headBottomScaleY,
                                  headLeftScaleX, headLeftScaleY, headRightScaleX, headRightScaleY),
                LimbType.Torso => (torsoFrontScaleX, torsoFrontScaleY, torsoBackScaleX, torsoBackScaleY,
                                   torsoTopScaleX, torsoTopScaleY, torsoBottomScaleX, torsoBottomScaleY,
                                   torsoLeftScaleX, torsoLeftScaleY, torsoRightScaleX, torsoRightScaleY),
                LimbType.Waist => (waistFrontScaleX, waistFrontScaleY, waistBackScaleX, waistBackScaleY,
                                   waistTopScaleX, waistTopScaleY, waistBottomScaleX, waistBottomScaleY,
                                   waistLeftScaleX, waistLeftScaleY, waistRightScaleX, waistRightScaleY),
                LimbType.LeftArm => (leftArmFrontScaleX, leftArmFrontScaleY, leftArmBackScaleX, leftArmBackScaleY,
                                      leftArmTopScaleX, leftArmTopScaleY, leftArmBottomScaleX, leftArmBottomScaleY,
                                      leftArmLeftScaleX, leftArmLeftScaleY, leftArmRightScaleX, leftArmRightScaleY),
                LimbType.RightArm => (rightArmFrontScaleX, rightArmFrontScaleY, rightArmBackScaleX, rightArmBackScaleY,
                                       rightArmTopScaleX, rightArmTopScaleY, rightArmBottomScaleX, rightArmBottomScaleY,
                                       rightArmLeftScaleX, rightArmLeftScaleY, rightArmRightScaleX, rightArmRightScaleY),
                LimbType.LeftForearm => (leftForearmFrontScaleX, leftForearmFrontScaleY, leftForearmBackScaleX, leftForearmBackScaleY,
                                          leftForearmTopScaleX, leftForearmTopScaleY, leftForearmBottomScaleX, leftForearmBottomScaleY,
                                          leftForearmLeftScaleX, leftForearmLeftScaleY, leftForearmRightScaleX, leftForearmRightScaleY),
                LimbType.RightForearm => (rightForearmFrontScaleX, rightForearmFrontScaleY, rightForearmBackScaleX, rightForearmBackScaleY,
                                           rightForearmTopScaleX, rightForearmTopScaleY, rightForearmBottomScaleX, rightForearmBottomScaleY,
                                           rightForearmLeftScaleX, rightForearmLeftScaleY, rightForearmRightScaleX, rightForearmRightScaleY),
                LimbType.LeftHand => (leftHandFrontScaleX, leftHandFrontScaleY, leftHandBackScaleX, leftHandBackScaleY,
                                       leftHandTopScaleX, leftHandTopScaleY, leftHandBottomScaleX, leftHandBottomScaleY,
                                       leftHandLeftScaleX, leftHandLeftScaleY, leftHandRightScaleX, leftHandRightScaleY),
                LimbType.RightHand => (rightHandFrontScaleX, rightHandFrontScaleY, rightHandBackScaleX, rightHandBackScaleY,
                                        rightHandTopScaleX, rightHandTopScaleY, rightHandBottomScaleX, rightHandBottomScaleY,
                                        rightHandLeftScaleX, rightHandLeftScaleY, rightHandRightScaleX, rightHandRightScaleY),
                LimbType.LeftThigh => (leftThighFrontScaleX, leftThighFrontScaleY, leftThighBackScaleX, leftThighBackScaleY,
                                        leftThighTopScaleX, leftThighTopScaleY, leftThighBottomScaleX, leftThighBottomScaleY,
                                        leftThighLeftScaleX, leftThighLeftScaleY, leftThighRightScaleX, leftThighRightScaleY),
                LimbType.RightThigh => (rightThighFrontScaleX, rightThighFrontScaleY, rightThighBackScaleX, rightThighBackScaleY,
                                         rightThighTopScaleX, rightThighTopScaleY, rightThighBottomScaleX, rightThighBottomScaleY,
                                         rightThighLeftScaleX, rightThighLeftScaleY, rightThighRightScaleX, rightThighRightScaleY),
                LimbType.LeftLeg => (leftLegFrontScaleX, leftLegFrontScaleY, leftLegBackScaleX, leftLegBackScaleY,
                                      leftLegTopScaleX, leftLegTopScaleY, leftLegBottomScaleX, leftLegBottomScaleY,
                                      leftLegLeftScaleX, leftLegLeftScaleY, leftLegRightScaleX, leftLegRightScaleY),
                LimbType.RightLeg => (rightLegFrontScaleX, rightLegFrontScaleY, rightLegBackScaleX, rightLegBackScaleY,
                                       rightLegTopScaleX, rightLegTopScaleY, rightLegBottomScaleX, rightLegBottomScaleY,
                                       rightLegLeftScaleX, rightLegLeftScaleY, rightLegRightScaleX, rightLegRightScaleY),
                LimbType.LeftFoot => (leftFootFrontScaleX, leftFootFrontScaleY, leftFootBackScaleX, leftFootBackScaleY,
                                       leftFootTopScaleX, leftFootTopScaleY, leftFootBottomScaleX, leftFootBottomScaleY,
                                       leftFootLeftScaleX, leftFootLeftScaleY, leftFootRightScaleX, leftFootRightScaleY),
                LimbType.RightFoot => (rightFootFrontScaleX, rightFootFrontScaleY, rightFootBackScaleX, rightFootBackScaleY,
                                        rightFootTopScaleX, rightFootTopScaleY, rightFootBottomScaleX, rightFootBottomScaleY,
                                        rightFootLeftScaleX, rightFootLeftScaleY, rightFootRightScaleX, rightFootRightScaleY),
                _ => (1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f)
            };
        }

        // ==================== РАЗМЕРЫ ГРАНЕЙ ДЛЯ КАЖДОЙ ЧАСТИ ====================
        // ScaleX = растяжение по горизонтали, ScaleY = растяжение по вертикали
        // ==========================================================================
        
        // ГОЛОВА - размеры граней
        private static float headFrontScaleX = 1f, headFrontScaleY = 1f;
        private static float headBackScaleX = 1f, headBackScaleY = 1f;
        private static float headTopScaleX = 1f, headTopScaleY = 1f;
        private static float headBottomScaleX = 1f, headBottomScaleY = 1f;
        private static float headLeftScaleX = 1f, headLeftScaleY = 1f;
        private static float headRightScaleX = 1f, headRightScaleY = 1f;
        
        // ТУЛОВИЩЕ - размеры граней
        private static float torsoFrontScaleX = 1f, torsoFrontScaleY = 1f;
        private static float torsoBackScaleX = 1f, torsoBackScaleY = 1f;
        private static float torsoTopScaleX = 1f, torsoTopScaleY = 1f;
        private static float torsoBottomScaleX = 1f, torsoBottomScaleY = 1f;
        private static float torsoLeftScaleX = 1f, torsoLeftScaleY = 1f;
        private static float torsoRightScaleX = 1f, torsoRightScaleY = 1f;
        
        // ТАЛИЯ - размеры граней
        private static float waistFrontScaleX = 1f, waistFrontScaleY = 1f;
        private static float waistBackScaleX = 1f, waistBackScaleY = 1f;
        private static float waistTopScaleX = 1f, waistTopScaleY = 1f;
        private static float waistBottomScaleX = 1f, waistBottomScaleY = 1f;
        private static float waistLeftScaleX = 1f, waistLeftScaleY = 1f;
        private static float waistRightScaleX = 1f, waistRightScaleY = 1f;
        
        // ЛЕВОЕ ПЛЕЧО - размеры граней
        private static float leftArmFrontScaleX = 1f, leftArmFrontScaleY = 1f;
        private static float leftArmBackScaleX = 1f, leftArmBackScaleY = 1f;
        private static float leftArmTopScaleX = 1f, leftArmTopScaleY = 1f;
        private static float leftArmBottomScaleX = 1f, leftArmBottomScaleY = 1f;
        private static float leftArmLeftScaleX = 1f, leftArmLeftScaleY = 1f;
        private static float leftArmRightScaleX = 1f, leftArmRightScaleY = 1f;
        
        // ПРАВОЕ ПЛЕЧО - размеры граней
        private static float rightArmFrontScaleX = 1f, rightArmFrontScaleY = 1f;
        private static float rightArmBackScaleX = 1f, rightArmBackScaleY = 1f;
        private static float rightArmTopScaleX = 1f, rightArmTopScaleY = 1f;
        private static float rightArmBottomScaleX = 1f, rightArmBottomScaleY = 1f;
        private static float rightArmLeftScaleX = 1f, rightArmLeftScaleY = 1f;
        private static float rightArmRightScaleX = 1f, rightArmRightScaleY = 1f;
        
        // ЛЕВОЕ ПРЕДПЛЕЧЬЕ - размеры граней
        private static float leftForearmFrontScaleX = 1f, leftForearmFrontScaleY = 1f;
        private static float leftForearmBackScaleX = 1f, leftForearmBackScaleY = 1f;
        private static float leftForearmTopScaleX = 1f, leftForearmTopScaleY = 1f;
        private static float leftForearmBottomScaleX = 1f, leftForearmBottomScaleY = 1f;
        private static float leftForearmLeftScaleX = 1f, leftForearmLeftScaleY = 1f;
        private static float leftForearmRightScaleX = 1f, leftForearmRightScaleY = 1f;
        
        // ПРАВОЕ ПРЕДПЛЕЧЬЕ - размеры граней
        private static float rightForearmFrontScaleX = 1f, rightForearmFrontScaleY = 1f;
        private static float rightForearmBackScaleX = 1f, rightForearmBackScaleY = 1f;
        private static float rightForearmTopScaleX = 1f, rightForearmTopScaleY = 1f;
        private static float rightForearmBottomScaleX = 1f, rightForearmBottomScaleY = 1f;
        private static float rightForearmLeftScaleX = 1f, rightForearmLeftScaleY = 1f;
        private static float rightForearmRightScaleX = 1f, rightForearmRightScaleY = 1f;
        
        // ЛЕВАЯ КИСТЬ - размеры граней
        private static float leftHandFrontScaleX = 1f, leftHandFrontScaleY = 1f;
        private static float leftHandBackScaleX = 1f, leftHandBackScaleY = 1f;
        private static float leftHandTopScaleX = 1f, leftHandTopScaleY = 1f;
        private static float leftHandBottomScaleX = 1f, leftHandBottomScaleY = 1f;
        private static float leftHandLeftScaleX = 1f, leftHandLeftScaleY = 1f;
        private static float leftHandRightScaleX = 1f, leftHandRightScaleY = 1f;
        
        // ПРАВАЯ КИСТЬ - размеры граней
        private static float rightHandFrontScaleX = 1f, rightHandFrontScaleY = 1f;
        private static float rightHandBackScaleX = 1f, rightHandBackScaleY = 1f;
        private static float rightHandTopScaleX = 1f, rightHandTopScaleY = 1f;
        private static float rightHandBottomScaleX = 1f, rightHandBottomScaleY = 1f;
        private static float rightHandLeftScaleX = 1f, rightHandLeftScaleY = 1f;
        private static float rightHandRightScaleX = 1f, rightHandRightScaleY = 1f;
        
        // ЛЕВОЕ БЕДРО - размеры граней
        private static float leftThighFrontScaleX = 1f, leftThighFrontScaleY = 1f;
        private static float leftThighBackScaleX = 1f, leftThighBackScaleY = 1f;
        private static float leftThighTopScaleX = 1f, leftThighTopScaleY = 1f;
        private static float leftThighBottomScaleX = 1f, leftThighBottomScaleY = 1f;
        private static float leftThighLeftScaleX = 1f, leftThighLeftScaleY = 1f;
        private static float leftThighRightScaleX = 1f, leftThighRightScaleY = 1f;
        
        // ПРАВОЕ БЕДРО - размеры граней
        private static float rightThighFrontScaleX = 1f, rightThighFrontScaleY = 1f;
        private static float rightThighBackScaleX = 1f, rightThighBackScaleY = 1f;
        private static float rightThighTopScaleX = 1f, rightThighTopScaleY = 1f;
        private static float rightThighBottomScaleX = 1f, rightThighBottomScaleY = 1f;
        private static float rightThighLeftScaleX = 1f, rightThighLeftScaleY = 1f;
        private static float rightThighRightScaleX = 1f, rightThighRightScaleY = 1f;
        
        // ЛЕВАЯ ГОЛЕНЬ - размеры граней
        private static float leftLegFrontScaleX = 1f, leftLegFrontScaleY = 1f;
        private static float leftLegBackScaleX = 1f, leftLegBackScaleY = 1f;
        private static float leftLegTopScaleX = 1f, leftLegTopScaleY = 1f;
        private static float leftLegBottomScaleX = 1f, leftLegBottomScaleY = 1f;
        private static float leftLegLeftScaleX = 1f, leftLegLeftScaleY = 1f;
        private static float leftLegRightScaleX = 1f, leftLegRightScaleY = 1f;
        
        // ПРАВАЯ ГОЛЕНЬ - размеры граней
        private static float rightLegFrontScaleX = 1f, rightLegFrontScaleY = 1f;
        private static float rightLegBackScaleX = 1f, rightLegBackScaleY = 1f;
        private static float rightLegTopScaleX = 1f, rightLegTopScaleY = 1f;
        private static float rightLegBottomScaleX = 1f, rightLegBottomScaleY = 1f;
        private static float rightLegLeftScaleX = 1f, rightLegLeftScaleY = 1f;
        private static float rightLegRightScaleX = 1f, rightLegRightScaleY = 1f;
        
        // ЛЕВАЯ СТОПА - размеры граней
        private static float leftFootFrontScaleX = 1f, leftFootFrontScaleY = 1f;
        private static float leftFootBackScaleX = 1f, leftFootBackScaleY = 1f;
        private static float leftFootTopScaleX = 1f, leftFootTopScaleY = 1f;
        private static float leftFootBottomScaleX = 1f, leftFootBottomScaleY = 1f;
        private static float leftFootLeftScaleX = 1f, leftFootLeftScaleY = 1f;
        private static float leftFootRightScaleX = 1f, leftFootRightScaleY = 1f;
        
        // ПРАВАЯ СТОПА - размеры граней
        private static float rightFootFrontScaleX = 1f, rightFootFrontScaleY = 1f;
        private static float rightFootBackScaleX = 1f, rightFootBackScaleY = 1f;
        private static float rightFootTopScaleX = 1f, rightFootTopScaleY = 1f;
        private static float rightFootBottomScaleX = 1f, rightFootBottomScaleY = 1f;
        private static float rightFootLeftScaleX = 1f, rightFootLeftScaleY = 1f;
        private static float rightFootRightScaleX = 1f, rightFootRightScaleY = 1f;
        
        // Режим рендера персонажей
        private static int characterDepthMode = 0;


        
        private static string GetFreezeModeName(int mode) => mode switch
        {
            0 => "No freeze", 1 => "3 frames", 2 => "5 frames",
            3 => "8 frames", 4 => "12 frames", 5 => "20 frames", _ => "Unknown"
        };
        private static float GetSelectedFaceOffset()
        {
            return (limbDisplayMode, selectedSubPart, selectedFace) switch
            {
                // HEAD
                (1, 0, 0) => headFrontOffset,
                (1, 0, 1) => headBackOffset,
                (1, 0, 2) => headTopOffset,
                (1, 0, 3) => headBottomOffset,
                (1, 0, 4) => headLeftOffset,
                (1, 0, 5) => headRightOffset,

                // TORSO
                (2, 0, 0) => torsoFrontOffset,
                (2, 0, 1) => torsoBackOffset,
                (2, 0, 2) => torsoTopOffset,
                (2, 0, 3) => torsoBottomOffset,
                (2, 0, 4) => torsoLeftOffset,
                (2, 0, 5) => torsoRightOffset,

                // WAIST
                (2, 1, 0) => waistFrontOffset,
                (2, 1, 1) => waistBackOffset,
                (2, 1, 2) => waistTopOffset,
                (2, 1, 3) => waistBottomOffset,
                (2, 1, 4) => waistLeftOffset,
                (2, 1, 5) => waistRightOffset,

                // LEFT ARM
                (3, 0, 0) => leftArmFrontOffset,
                (3, 0, 1) => leftArmBackOffset,
                (3, 0, 2) => leftArmTopOffset,
                (3, 0, 3) => leftArmBottomOffset,
                (3, 0, 4) => leftArmLeftOffset,
                (3, 0, 5) => leftArmRightOffset,

                // LEFT FOREARM
                (3, 1, 0) => leftForearmFrontOffset,
                (3, 1, 1) => leftForearmBackOffset,
                (3, 1, 2) => leftForearmTopOffset,
                (3, 1, 3) => leftForearmBottomOffset,
                (3, 1, 4) => leftForearmLeftOffset,
                (3, 1, 5) => leftForearmRightOffset,

                // LEFT HAND
                (3, 2, 0) => leftHandFrontOffset,
                (3, 2, 1) => leftHandBackOffset,
                (3, 2, 2) => leftHandTopOffset,
                (3, 2, 3) => leftHandBottomOffset,
                (3, 2, 4) => leftHandLeftOffset,
                (3, 2, 5) => leftHandRightOffset,

                // RIGHT ARM
                (4, 0, 0) => rightArmFrontOffset,
                (4, 0, 1) => rightArmBackOffset,
                (4, 0, 2) => rightArmTopOffset,
                (4, 0, 3) => rightArmBottomOffset,
                (4, 0, 4) => rightArmLeftOffset,
                (4, 0, 5) => rightArmRightOffset,

                // RIGHT FOREARM
                (4, 1, 0) => rightForearmFrontOffset,
                (4, 1, 1) => rightForearmBackOffset,
                (4, 1, 2) => rightForearmTopOffset,
                (4, 1, 3) => rightForearmBottomOffset,
                (4, 1, 4) => rightForearmLeftOffset,
                (4, 1, 5) => rightForearmRightOffset,

                // RIGHT HAND
                (4, 2, 0) => rightHandFrontOffset,
                (4, 2, 1) => rightHandBackOffset,
                (4, 2, 2) => rightHandTopOffset,
                (4, 2, 3) => rightHandBottomOffset,
                (4, 2, 4) => rightHandLeftOffset,
                (4, 2, 5) => rightHandRightOffset,

                // LEFT THIGH
                (5, 0, 0) => leftThighFrontOffset,
                (5, 0, 1) => leftThighBackOffset,
                (5, 0, 2) => leftThighTopOffset,
                (5, 0, 3) => leftThighBottomOffset,
                (5, 0, 4) => leftThighLeftOffset,
                (5, 0, 5) => leftThighRightOffset,

                // LEFT LEG
                (5, 1, 0) => leftLegFrontOffset,
                (5, 1, 1) => leftLegBackOffset,
                (5, 1, 2) => leftLegTopOffset,
                (5, 1, 3) => leftLegBottomOffset,
                (5, 1, 4) => leftLegLeftOffset,
                (5, 1, 5) => leftLegRightOffset,

                // LEFT FOOT
                (5, 2, 0) => leftFootFrontOffset,
                (5, 2, 1) => leftFootBackOffset,
                (5, 2, 2) => leftFootTopOffset,
                (5, 2, 3) => leftFootBottomOffset,
                (5, 2, 4) => leftFootLeftOffset,
                (5, 2, 5) => leftFootRightOffset,

                // RIGHT THIGH
                (6, 0, 0) => rightThighFrontOffset,
                (6, 0, 1) => rightThighBackOffset,
                (6, 0, 2) => rightThighTopOffset,
                (6, 0, 3) => rightThighBottomOffset,
                (6, 0, 4) => rightThighLeftOffset,
                (6, 0, 5) => rightThighRightOffset,

                // RIGHT LEG
                (6, 1, 0) => rightLegFrontOffset,
                (6, 1, 1) => rightLegBackOffset,
                (6, 1, 2) => rightLegTopOffset,
                (6, 1, 3) => rightLegBottomOffset,
                (6, 1, 4) => rightLegLeftOffset,
                (6, 1, 5) => rightLegRightOffset,

                // RIGHT FOOT
                (6, 2, 0) => rightFootFrontOffset,
                (6, 2, 1) => rightFootBackOffset,
                (6, 2, 2) => rightFootTopOffset,
                (6, 2, 3) => rightFootBottomOffset,
                (6, 2, 4) => rightFootLeftOffset,
                (6, 2, 5) => rightFootRightOffset,

                _ => 0f
            };
        }

        private static void SetSelectedFaceOffset(float value)
        {
            switch (limbDisplayMode, selectedSubPart, selectedFace)
            {
                // HEAD
                case (1, 0, 0): headFrontOffset = value; break;
                case (1, 0, 1): headBackOffset = value; break;
                case (1, 0, 2): headTopOffset = value; break;
                case (1, 0, 3): headBottomOffset = value; break;
                case (1, 0, 4): headLeftOffset = value; break;
                case (1, 0, 5): headRightOffset = value; break;

                // TORSO
                case (2, 0, 0): torsoFrontOffset = value; break;
                case (2, 0, 1): torsoBackOffset = value; break;
                case (2, 0, 2): torsoTopOffset = value; break;
                case (2, 0, 3): torsoBottomOffset = value; break;
                case (2, 0, 4): torsoLeftOffset = value; break;
                case (2, 0, 5): torsoRightOffset = value; break;

                // WAIST
                case (2, 1, 0): waistFrontOffset = value; break;
                case (2, 1, 1): waistBackOffset = value; break;
                case (2, 1, 2): waistTopOffset = value; break;
                case (2, 1, 3): waistBottomOffset = value; break;
                case (2, 1, 4): waistLeftOffset = value; break;
                case (2, 1, 5): waistRightOffset = value; break;

                // LEFT ARM
                case (3, 0, 0): leftArmFrontOffset = value; break;
                case (3, 0, 1): leftArmBackOffset = value; break;
                case (3, 0, 2): leftArmTopOffset = value; break;
                case (3, 0, 3): leftArmBottomOffset = value; break;
                case (3, 0, 4): leftArmLeftOffset = value; break;
                case (3, 0, 5): leftArmRightOffset = value; break;

                // LEFT FOREARM
                case (3, 1, 0): leftForearmFrontOffset = value; break;
                case (3, 1, 1): leftForearmBackOffset = value; break;
                case (3, 1, 2): leftForearmTopOffset = value; break;
                case (3, 1, 3): leftForearmBottomOffset = value; break;
                case (3, 1, 4): leftForearmLeftOffset = value; break;
                case (3, 1, 5): leftForearmRightOffset = value; break;

                // LEFT HAND
                case (3, 2, 0): leftHandFrontOffset = value; break;
                case (3, 2, 1): leftHandBackOffset = value; break;
                case (3, 2, 2): leftHandTopOffset = value; break;
                case (3, 2, 3): leftHandBottomOffset = value; break;
                case (3, 2, 4): leftHandLeftOffset = value; break;
                case (3, 2, 5): leftHandRightOffset = value; break;

                // RIGHT ARM
                case (4, 0, 0): rightArmFrontOffset = value; break;
                case (4, 0, 1): rightArmBackOffset = value; break;
                case (4, 0, 2): rightArmTopOffset = value; break;
                case (4, 0, 3): rightArmBottomOffset = value; break;
                case (4, 0, 4): rightArmLeftOffset = value; break;
                case (4, 0, 5): rightArmRightOffset = value; break;

                // RIGHT FOREARM
                case (4, 1, 0): rightForearmFrontOffset = value; break;
                case (4, 1, 1): rightForearmBackOffset = value; break;
                case (4, 1, 2): rightForearmTopOffset = value; break;
                case (4, 1, 3): rightForearmBottomOffset = value; break;
                case (4, 1, 4): rightForearmLeftOffset = value; break;
                case (4, 1, 5): rightForearmRightOffset = value; break;

                // RIGHT HAND
                case (4, 2, 0): rightHandFrontOffset = value; break;
                case (4, 2, 1): rightHandBackOffset = value; break;
                case (4, 2, 2): rightHandTopOffset = value; break;
                case (4, 2, 3): rightHandBottomOffset = value; break;
                case (4, 2, 4): rightHandLeftOffset = value; break;
                case (4, 2, 5): rightHandRightOffset = value; break;

                // LEFT THIGH
                case (5, 0, 0): leftThighFrontOffset = value; break;
                case (5, 0, 1): leftThighBackOffset = value; break;
                case (5, 0, 2): leftThighTopOffset = value; break;
                case (5, 0, 3): leftThighBottomOffset = value; break;
                case (5, 0, 4): leftThighLeftOffset = value; break;
                case (5, 0, 5): leftThighRightOffset = value; break;

                // LEFT LEG
                case (5, 1, 0): leftLegFrontOffset = value; break;
                case (5, 1, 1): leftLegBackOffset = value; break;
                case (5, 1, 2): leftLegTopOffset = value; break;
                case (5, 1, 3): leftLegBottomOffset = value; break;
                case (5, 1, 4): leftLegLeftOffset = value; break;
                case (5, 1, 5): leftLegRightOffset = value; break;

                // LEFT FOOT
                case (5, 2, 0): leftFootFrontOffset = value; break;
                case (5, 2, 1): leftFootBackOffset = value; break;
                case (5, 2, 2): leftFootTopOffset = value; break;
                case (5, 2, 3): leftFootBottomOffset = value; break;
                case (5, 2, 4): leftFootLeftOffset = value; break;
                case (5, 2, 5): leftFootRightOffset = value; break;

                // RIGHT THIGH
                case (6, 0, 0): rightThighFrontOffset = value; break;
                case (6, 0, 1): rightThighBackOffset = value; break;
                case (6, 0, 2): rightThighTopOffset = value; break;
                case (6, 0, 3): rightThighBottomOffset = value; break;
                case (6, 0, 4): rightThighLeftOffset = value; break;
                case (6, 0, 5): rightThighRightOffset = value; break;

                // RIGHT LEG
                case (6, 1, 0): rightLegFrontOffset = value; break;
                case (6, 1, 1): rightLegBackOffset = value; break;
                case (6, 1, 2): rightLegTopOffset = value; break;
                case (6, 1, 3): rightLegBottomOffset = value; break;
                case (6, 1, 4): rightLegLeftOffset = value; break;
                case (6, 1, 5): rightLegRightOffset = value; break;

                // RIGHT FOOT
                case (6, 2, 0): rightFootFrontOffset = value; break;
                case (6, 2, 1): rightFootBackOffset = value; break;
                case (6, 2, 2): rightFootTopOffset = value; break;
                case (6, 2, 3): rightFootBottomOffset = value; break;
                case (6, 2, 4): rightFootLeftOffset = value; break;
                case (6, 2, 5): rightFootRightOffset = value; break;
            }
        }

        // Получить все смещения граней для части тела
        private static (float front, float back, float top, float bottom, float left, float right) GetFaceOffsets(LimbType limbType)
        {
            return limbType switch
            {
                LimbType.Head => (headFrontOffset, headBackOffset, headTopOffset, headBottomOffset, headLeftOffset, headRightOffset),
                LimbType.Torso => (torsoFrontOffset, torsoBackOffset, torsoTopOffset, torsoBottomOffset, torsoLeftOffset, torsoRightOffset),
                LimbType.Waist => (waistFrontOffset, waistBackOffset, waistTopOffset, waistBottomOffset, waistLeftOffset, waistRightOffset),
                LimbType.LeftArm => (leftArmFrontOffset, leftArmBackOffset, leftArmTopOffset, leftArmBottomOffset, leftArmLeftOffset, leftArmRightOffset),
                LimbType.RightArm => (rightArmFrontOffset, rightArmBackOffset, rightArmTopOffset, rightArmBottomOffset, rightArmLeftOffset, rightArmRightOffset),
                LimbType.LeftForearm => (leftForearmFrontOffset, leftForearmBackOffset, leftForearmTopOffset, leftForearmBottomOffset, leftForearmLeftOffset, leftForearmRightOffset),
                LimbType.RightForearm => (rightForearmFrontOffset, rightForearmBackOffset, rightForearmTopOffset, rightForearmBottomOffset, rightForearmLeftOffset, rightForearmRightOffset),
                LimbType.LeftHand => (leftHandFrontOffset, leftHandBackOffset, leftHandTopOffset, leftHandBottomOffset, leftHandLeftOffset, leftHandRightOffset),
                LimbType.RightHand => (rightHandFrontOffset, rightHandBackOffset, rightHandTopOffset, rightHandBottomOffset, rightHandLeftOffset, rightHandRightOffset),
                LimbType.LeftThigh => (leftThighFrontOffset, leftThighBackOffset, leftThighTopOffset, leftThighBottomOffset, leftThighLeftOffset, leftThighRightOffset),
                LimbType.RightThigh => (rightThighFrontOffset, rightThighBackOffset, rightThighTopOffset, rightThighBottomOffset, rightThighLeftOffset, rightThighRightOffset),
                LimbType.LeftLeg => (leftLegFrontOffset, leftLegBackOffset, leftLegTopOffset, leftLegBottomOffset, leftLegLeftOffset, leftLegRightOffset),
                LimbType.RightLeg => (rightLegFrontOffset, rightLegBackOffset, rightLegTopOffset, rightLegBottomOffset, rightLegLeftOffset, rightLegRightOffset),
                LimbType.LeftFoot => (leftFootFrontOffset, leftFootBackOffset, leftFootTopOffset, leftFootBottomOffset, leftFootLeftOffset, leftFootRightOffset),
                LimbType.RightFoot => (rightFootFrontOffset, rightFootBackOffset, rightFootTopOffset, rightFootBottomOffset, rightFootLeftOffset, rightFootRightOffset),
                _ => (0f, 0f, 0f, 0f, 0f, 0f)
            };
        }
        private const float DOOR_SCALE_X = 1.0f;
        private const float DOOR_SCALE_Y = 1.0f;
        private const float DOOR_SCALE_Z = 1.0f;

        private static int doorRenderMode = 1;

        private static readonly Dictionary<string, (float x, float y, float z, bool useWindowMode)> customDoorScales = 
            new Dictionary<string, (float, float, float, bool)>
        {
            { "windoweddoorwbuttons", (1.0f, 1.0f, 1.0f, true) },
        };

        private static bool showNearbyDoors = false;
        private static List<(string id, string name, float dist)> nearbyDoorsList = new List<(string, string, float)>();

        public FirstPersonCamera()
        {
            transformField = typeof(Camera).GetField("transform", BindingFlags.NonPublic | BindingFlags.Instance);
            shaderTransformField = typeof(Camera).GetField("shaderTransform", BindingFlags.NonPublic | BindingFlags.Instance);
            doorSpriteField = typeof(Door).GetField("doorSprite", BindingFlags.NonPublic | BindingFlags.Instance);

            //AddInteractionInputTypesToBlockSet();

			
            depthWriteState = new DepthStencilState
            {
                DepthBufferEnable = true,
                DepthBufferWriteEnable = true,
                DepthBufferFunction = CompareFunction.LessEqual
            };
            
            GameMain.LuaCs.Hook.Add("think", "fp_update", (object[] args) => 
            { 
                try { OnUpdate(); } catch (Exception ex) { DebugConsole.NewMessage("FP err: " + ex.Message, Color.Red); } 
                return null; 
            });
            
            try
            {
                var updateTransformMethod = typeof(Camera).GetMethod("UpdateTransform", BindingFlags.Public | BindingFlags.Instance);
                if (updateTransformMethod != null)
                {
                    GameMain.LuaCs.Hook.HookMethod("fp_transform", updateTransformMethod, 
                        (object inst, Dictionary<string, object> args) => 
                        {
                            if (isFirstPerson && Character.Controlled != null && !show2DWindow) 
                                OverrideTransform((Camera)inst);
                            return null;
                        }, LuaCsHook.HookMethodType.After);
                }
                
                var drawMethod = typeof(GameScreen).GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance, 
                    null, new Type[] { typeof(double), typeof(GraphicsDevice), typeof(SpriteBatch) }, null);
                if (drawMethod != null)
                {
                    GameMain.LuaCs.Hook.HookMethod("fp_postdraw", drawMethod, 
                        (object inst, Dictionary<string, object> args) =>
                        {
                            if (isFirstPerson && Character.Controlled != null) 
                            {
                                DrawOverlay();
                                if (show2DWindow) Draw2DDebugWindow();
                            }
                            return null;
                        }, LuaCsHook.HookMethodType.After);
                }
                
                var drawMapMethod = typeof(GameScreen).GetMethod("DrawMap", BindingFlags.Public | BindingFlags.Instance);
                if (drawMapMethod != null)
                {
                    GameMain.LuaCs.Hook.HookMethod("fp_postdrawmap", drawMapMethod, 
                        (object inst, Dictionary<string, object> args) => 
                        {
                            if (isFirstPerson && Character.Controlled != null) 
                            {
                                if (show2DWindow) try { Render2DToTexture(); } catch { }
                                try { Draw3DWorld(); } catch { }
                            }
                            return null;
                        }, LuaCsHook.HookMethodType.After);
                }
                
                var flipMethod = typeof(Ragdoll).GetMethod("Flip", BindingFlags.Public | BindingFlags.Instance);
                if (flipMethod != null)
                {
                    GameMain.LuaCs.Hook.HookMethod("fp_blockflip", flipMethod, 
                        (object inst, Dictionary<string, object> args) =>
                        {
                            if (!blockFlip) return null;
                            try
                            {
                                var ragdoll = (Ragdoll)inst;
                                if (ragdoll.Character == Character.Controlled)
                                    return new object();
                            }
                            catch { }
                            return null;
                        }, LuaCsHook.HookMethodType.Before);
                }

                Type keyType = null;
                var keysProperty = typeof(Character).GetProperty("Keys");
                if (keysProperty != null)
                    keyType = keysProperty.PropertyType.GetElementType();
                
                if (keyType != null)
                {
                    keyInputTypeField = keyType.GetField("inputType", BindingFlags.NonPublic | BindingFlags.Instance);
                    var setStateMethod = keyType.GetMethod("SetState", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (setStateMethod != null)
                    {
                        GameMain.LuaCs.Hook.HookMethod("fp_blocksetstate", setStateMethod,
                            (object inst, Dictionary<string, object> args) =>
                            {
                                if (!blockKeySetState || keyInputTypeField == null) return null;
                                try
                                {
                                    var inputType = (InputType)keyInputTypeField.GetValue(inst);
                                    if (controlledInputs.Contains(inputType))
                                        return new object();
                                }
                                catch { }
                                return null;
                            }, LuaCsHook.HookMethodType.Before);
                    }
                }
            }
            catch (Exception ex) 
            { 
                DebugConsole.NewMessage("FP3D: Hook error: " + ex.Message, Color.Red);
            }
            
            DebugConsole.NewMessage("FP3D: F5=3D | NumPad3=CharMode | NumPad5=DoorList | F10=debug", Color.Cyan);
        }
        private static void DrawTexturedFaceAlphaTest(
            GraphicsDevice gd,
            Texture2D tex,
            Matrix world,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br,
            float u0, float v0, float u1, float v1,
            Color color)
        {
            alphaTestEffect.World = world;
            alphaTestEffect.View = basicEffect.View;
            alphaTestEffect.Projection = basicEffect.Projection;
            alphaTestEffect.Texture = tex;

            // Ключ: отсекаем прозрачные пиксели, они НЕ пишут depth
            alphaTestEffect.AlphaFunction = CompareFunction.Greater;
            alphaTestEffect.ReferenceAlpha = 999;

            quadVertices[0] = new VertexPositionColorTexture(tl, color, new Vector2(u0, v0));
            quadVertices[1] = new VertexPositionColorTexture(tr, color, new Vector2(u1, v0));
            quadVertices[2] = new VertexPositionColorTexture(bl, color, new Vector2(u0, v1));
            quadVertices[3] = new VertexPositionColorTexture(br, color, new Vector2(u1, v1));

            foreach (EffectPass pass in alphaTestEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleList,
                    quadVertices, 0, 4,
                    quadIndices, 0, 2);
            }
        }

        private static (float zOffset, float yOffset) GetLimbOffsets(LimbType t)
        {
            return t switch
            {
                LimbType.Head => (headOffset, headYOffset),
                LimbType.Torso => (torsoOffset, torsoYOffset),
                LimbType.Waist => (waistOffset, waistYOffset),
        
                LimbType.LeftArm => (leftArmOffset, leftArmYOffset),
                LimbType.LeftForearm => (leftForearmOffset, leftForearmYOffset),
                LimbType.LeftHand => (leftHandOffset, leftHandYOffset),
        
                LimbType.RightArm => (rightArmOffset, rightArmYOffset),
                LimbType.RightForearm => (rightForearmOffset, rightForearmYOffset),
                LimbType.RightHand => (rightHandOffset, rightHandYOffset),
        
                LimbType.LeftThigh => (leftThighOffset, leftThighYOffset),
                LimbType.LeftLeg => (leftLegOffset, leftLegYOffset),
                LimbType.LeftFoot => (leftFootOffset, leftFootYOffset),
        
                LimbType.RightThigh => (rightThighOffset, rightThighYOffset),
                LimbType.RightLeg => (rightLegOffset, rightLegYOffset),
                LimbType.RightFoot => (rightFootOffset, rightFootYOffset),
        
                _ => (0f, 0f)
            };
        }


        public override void Stop()
        {
            if (isFirstPerson) ExitFirstPerson();
            blockFlip = false;
            blockKeySetState = false;
            isFirstPerson = false;
            GameMain.LuaCs.Hook.Remove("think", "fp_update");
            pixelTexture?.Dispose();
            fpBatch?.Dispose();
            depthWriteState?.Dispose();
            depthReadState?.Dispose();
            debugRenderTarget?.Dispose();
            basicEffect?.Dispose();
            basicEffect = null;
            textureBoundsCache.Clear();
            extrudedMeshCache.Clear();
            spriteRegionPixelCache.Clear();
            alphaTestEffect?.Dispose();
            alphaTestEffect = null;
			armExtraRotDegState.Clear();
			
        }
		
        private static void SetupBasicEffect3D()
        {
            GetCameraVectors(out Vector3 cameraPos, out Vector3 forward, out Vector3 up);
            
            basicEffect.View = Matrix.CreateLookAt(cameraPos, cameraPos + forward * 100f, up);
            basicEffect.Projection = Matrix.CreatePerspectiveFieldOfView(
                fov, 
                (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight, 
                1f, 
                50000f);
            basicEffect.World = Matrix.Identity;
        }
        private static void EnsureResources()
        {
            var gd = GameMain.Instance.GraphicsDevice;

            if (pixelTexture == null || pixelTexture.IsDisposed)
            {
                pixelTexture = new Texture2D(gd, 1, 1);
                pixelTexture.SetData(new[] { Color.White });
            }

            if (fpBatch == null || fpBatch.IsDisposed)
                fpBatch = new SpriteBatch(gd);

            if (debugRenderTarget == null || debugRenderTarget.IsDisposed)
                debugRenderTarget = new RenderTarget2D(
                    gd,
                    debugWindowSize,
                    debugWindowSize,
                    false,
                    SurfaceFormat.Color,
                    DepthFormat.None);

            if (basicEffect == null || basicEffect.IsDisposed)
            {
                basicEffect = new BasicEffect(gd)
                {
                    TextureEnabled = true,
                    VertexColorEnabled = true
                };
            }

            // ВОТ ЭТОГО у тебя не было
            if (alphaTestEffect == null || alphaTestEffect.IsDisposed)
            {
                alphaTestEffect = new AlphaTestEffect(gd)
                {
                    VertexColorEnabled = true,
                    FogEnabled = false
                };
            }
        }


        private static Vector2 GetPullOffsetPx(LimbType t)
        {
            return t switch
            {
                LimbType.LeftArm => leftArmPullOffsetPx,
                LimbType.LeftForearm => leftForearmPullOffsetPx,
                LimbType.LeftHand => leftHandPullOffsetPx,
        
                LimbType.RightArm => rightArmPullOffsetPx,
                LimbType.RightForearm => rightForearmPullOffsetPx,
                LimbType.RightHand => rightHandPullOffsetPx,
        
                _ => Vector2.Zero
            };
        }

        private static void Render2DToTexture()
        {
            var gd = GameMain.Instance.GraphicsDevice;
            var ch = Character.Controlled;
            var cam = Screen.Selected?.Cam;
            if (ch == null || cam == null) return;

            EnsureResources();

            var oldTargets = gd.GetRenderTargets();
            gd.SetRenderTarget(debugRenderTarget);

            Color bgColor = new Color(20, 30, 40);
            try
            {
                if (Level.Loaded != null) bgColor = Level.Loaded.BackgroundColor;
            }
            catch { }

            gd.Clear(bgColor);

            Vector2 charPos = ch.WorldPosition;
            float zoom = 0.4f;

            Matrix viewMatrix =
                Matrix.CreateTranslation(-charPos.X, charPos.Y, 0f) *
                Matrix.CreateScale(zoom, zoom, 1f) *
                Matrix.CreateTranslation(debugWindowSize / 2f, debugWindowSize / 2f, 0f);

            // 1) BACK PASS
            fpBatch.Begin(
                SpriteSortMode.BackToFront,
                BlendState.NonPremultiplied,
                SamplerState.LinearWrap,
                null,
                RasterizerState.CullNone,
                null,
                viewMatrix);

            foreach (var entity in MapEntity.MapEntityList)
            {
                try
                {
                    if (entity?.Submarine == null) continue;

                    var wr = entity.WorldRect;
                    Vector2 cpos = new Vector2(wr.X + wr.Width * 0.5f, wr.Y - wr.Height * 0.5f);
                    if (Vector2.Distance(charPos, cpos) > 1800f) continue;

                    entity.Draw(fpBatch, false, true);
                }
                catch { }
            }

            fpBatch.End();

            // 2) FRONT PASS
            fpBatch.Begin(
                SpriteSortMode.BackToFront,
                BlendState.NonPremultiplied,
                SamplerState.LinearWrap,
                null,
                RasterizerState.CullNone,
                null,
                viewMatrix);

            foreach (var entity in MapEntity.MapEntityList)
            {
                try
                {
                    if (entity?.Submarine == null) continue;

                    var wr = entity.WorldRect;
                    Vector2 cpos = new Vector2(wr.X + wr.Width * 0.5f, wr.Y - wr.Height * 0.5f);
                    if (Vector2.Distance(charPos, cpos) > 1800f) continue;

                    entity.Draw(fpBatch, false, false);
                }
                catch { }
            }

            fpBatch.End();

            // 3) DAMAGE OVERLAY PASS (реальные данные section.damage / gap)
            fpBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.NonPremultiplied,
                null,
                null,
                RasterizerState.CullNone,
                null,
                viewMatrix);

            foreach (var st in Structure.WallList)
            {
                try
                {
                    if (st == null || st.Removed || st.Submarine == null) continue;
                    if (Vector2.Distance(charPos, st.WorldPosition) > 1800f) continue;
                    DrawStructureDamageOverlay2D(st);
                }
                catch { }
            }

            fpBatch.End();

            // 4) WATER PASS
            fpBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.NonPremultiplied,
                null,
                null,
                RasterizerState.CullNone,
                null,
                viewMatrix);

            foreach (var hull in Hull.HullList)
            {
                try
                {
                    if (hull?.Submarine == null) continue;
                    if (hull.WaterVolume <= 0f) continue;

                    var wr = hull.WorldRect;
                    float pct = MathHelper.Clamp(hull.WaterVolume / (hull.Rect.Width * hull.Rect.Height), 0f, 1f);
                    int waterH = (int)(wr.Height * pct);

                    fpBatch.Draw(
                        pixelTexture,
                        new Rectangle(wr.X, -(wr.Y - wr.Height + waterH), wr.Width, waterH),
                        new Color(30, 80, 120, 120));
                }
                catch { }
            }

            fpBatch.End();

            // 5) CHARACTERS
            fpBatch.Begin(
                SpriteSortMode.BackToFront,
                BlendState.NonPremultiplied,
                null,
                null,
                RasterizerState.CullNone,
                null,
                viewMatrix);

            foreach (var c in Character.CharacterList)
            {
                try
                {
                    if (!c.IsVisible) continue;
                    if (Vector2.Distance(charPos, c.WorldPosition) > 1800f) continue;
                    c.Draw(fpBatch, cam);
                }
                catch { }
            }

            fpBatch.End();

            // 6) DEBUG OVERLAY
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);

            int centerX = debugWindowSize / 2;
            int centerY = debugWindowSize / 2;

            float lookLen = 58f;
            float planarMul = MathF.Cos(camPitch);

            Vector2 lookPlanar = new Vector2(
                MathF.Cos(camYaw),
                -MathF.Sin(camYaw)) * (lookLen * planarMul);

            float pitchY = -MathF.Sin(camPitch) * (lookLen * 0.9f);

            Vector2 lookEnd = new Vector2(centerX, centerY) + lookPlanar + new Vector2(0f, pitchY);

            DrawLine(fpBatch, new Vector2(centerX, centerY), lookEnd, Color.Yellow, 3);

            Vector2 pitchOnlyEnd = new Vector2(centerX, centerY + pitchY);
            DrawLine(fpBatch, new Vector2(centerX, centerY), pitchOnlyEnd, Color.Orange, 2);

            float charDirX = (lockedDirection == Direction.Right ? 1f : -1f) * 30f;
            DrawLine(fpBatch, new Vector2(centerX, centerY), new Vector2(centerX + charDirX, centerY), Color.Cyan, 2);

            fpBatch.Draw(pixelTexture, new Rectangle(centerX - 5, centerY - 1, 10, 2), Color.White);
            fpBatch.Draw(pixelTexture, new Rectangle(centerX - 1, centerY - 5, 2, 10), Color.White);

            fpBatch.End();

            gd.SetRenderTargets(oldTargets);
        }



        private static void DrawLine(SpriteBatch sb, Vector2 start, Vector2 end, Color color, int thickness)
        {
            Vector2 edge = end - start;
            float angle = MathF.Atan2(edge.Y, edge.X);
            sb.Draw(pixelTexture, new Rectangle((int)start.X, (int)start.Y, (int)edge.Length(), thickness), null, color, angle, Vector2.Zero, SpriteEffects.None, 0);
        }

        private static void Draw2DDebugWindow()
        {
            if (debugRenderTarget == null || debugRenderTarget.IsDisposed) return;
            EnsureResources();
            int windowX = GameMain.GraphicsWidth - debugWindowSize - 20, windowY = 20;
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque);
            fpBatch.Draw(pixelTexture, new Rectangle(windowX - 3, windowY - 3, debugWindowSize + 6, debugWindowSize + 6), Color.White);
            fpBatch.Draw(pixelTexture, new Rectangle(windowX - 2, windowY - 2, debugWindowSize + 4, debugWindowSize + 4), Color.Black);
            fpBatch.Draw(debugRenderTarget, new Rectangle(windowX, windowY, debugWindowSize, debugWindowSize), Color.White);
            fpBatch.End();
        }

        private static Color MulOpaque(Color c, float m)
        {
            return new Color(
                (int)Math.Min(c.R * m, 255),
                (int)Math.Min(c.G * m, 255),
                (int)Math.Min(c.B * m, 255),
                255); // принудительно непрозрачно
        }

        private static void DrawBoxConnectors3D(Limb limb, Sprite sprite, float zCenter, float halfDepth, Color sideColor, Color topColor, Color bottomColor)
        {
            var gd = GameMain.Instance.GraphicsDevice;
            if (limb?.body == null || sprite?.Texture == null || sprite.Texture.IsDisposed) return;

            float scale = limb.Scale * limb.TextureScale;
            if (scale < 0.01f) scale = 1f;

            float rotation = -limb.body.DrawRotation;
            Vector2 pos = new Vector2(limb.body.DrawPosition.X, limb.body.DrawPosition.Y);

            // Те же локальные координаты, что и у caps
            float x0 = -sprite.Origin.X;
            float x1 = sprite.SourceRect.Width - sprite.Origin.X;
            float y0 = sprite.Origin.Y;
            float y1 = sprite.Origin.Y - sprite.SourceRect.Height;

            Matrix world =
                Matrix.CreateScale(scale, scale, 1f) *
                Matrix.CreateRotationZ(rotation) *
                Matrix.CreateTranslation(pos.X, pos.Y, zCenter);

            basicEffect.World = world;
            basicEffect.Texture = pixelTexture;

            Vector3 frontTL = new Vector3(x0, y0, +halfDepth);
            Vector3 frontTR = new Vector3(x1, y0, +halfDepth);
            Vector3 frontBL = new Vector3(x0, y1, +halfDepth);
            Vector3 frontBR = new Vector3(x1, y1, +halfDepth);

            Vector3 backTL = new Vector3(x0, y0, -halfDepth);
            Vector3 backTR = new Vector3(x1, y0, -halfDepth);
            Vector3 backBL = new Vector3(x0, y1, -halfDepth);
            Vector3 backBR = new Vector3(x1, y1, -halfDepth);

            // Левая стенка
            DrawColoredQuad(gd, backTL, frontTL, backBL, frontBL, sideColor);

            // Правая стенка
            DrawColoredQuad(gd, frontTR, backTR, frontBR, backBR, sideColor);

            // Верхняя стенка
            DrawColoredQuad(gd, backTL, backTR, frontTL, frontTR, topColor);

            // Нижняя стенка
            DrawColoredQuad(gd, frontBL, frontBR, backBL, backBR, bottomColor);
        }




        
        private static void ApplyArmPullAnchorOverrides(Character ch)
        {
            if (ch?.AnimController?.Limbs == null || pullJointField == null) return;
        
            foreach (var limb in ch.AnimController.Limbs)
            {
                if (limb?.body == null) continue;
        
                Vector2 offsPx = GetPullOffsetPx(limb.type);
                if (offsPx == Vector2.Zero) continue;
        
                object jointObj = pullJointField.GetValue(limb);
                if (jointObj == null) continue;
        
                var localAnchorProp = jointObj.GetType().GetProperty("LocalAnchorA");
                if (localAnchorProp == null || localAnchorProp.PropertyType != typeof(Vector2)) continue;
        
                if (!basePullAnchors.TryGetValue(limb, out Vector2 baseAnchor))
                {
                    baseAnchor = (Vector2)localAnchorProp.GetValue(jointObj);
                    basePullAnchors[limb] = baseAnchor;
                }
        
                Vector2 addSim = ConvertUnits.ToSimUnits(offsPx * limb.Scale);
                localAnchorProp.SetValue(jointObj, baseAnchor + addSim);
            }
        }





        private static void EnterFirstPerson()
        {
            var cam = Screen.Selected?.Cam;
            if (cam != null) { savedCameraPos = cam.Position; savedCameraZoom = cam.Zoom; }
            try
            {
                savedLosEnabled = GameMain.LightManager.LosEnabled;
                savedLightingEnabled = GameMain.LightManager.LightingEnabled;
                GameMain.LightManager.LosEnabled = false;
                GameMain.LightManager.LightingEnabled = false;
            }
            catch { }
            camOffset = Vector2.Zero;
            var ch = Character.Controlled;
            if (ch?.AnimController != null)
                lockedDirection = ch.AnimController.Dir > 0 ? Direction.Right : Direction.Left;
            blockFlip = true;
            blockKeySetState = true;
            flipCooldown = 0;
            flipFreezeFrames = 0;
            flipInProgress = false;
        }

        private static void ExitFirstPerson()
        {
            blockFlip = false;
            blockKeySetState = false;
            var cam = Screen.Selected?.Cam;
            if (cam != null) { cam.Position = savedCameraPos; cam.Zoom = savedCameraZoom; }
            try
            {
                GameMain.LightManager.LosEnabled = savedLosEnabled;
                GameMain.LightManager.LightingEnabled = savedLightingEnabled;
            }
            catch { }
        }

        private static void DoFlip(Character ch, Direction newDir)
        {
            if (ch?.AnimController == null || flipCooldown > 0 || flipInProgress) return;
            var anim = ch.AnimController;
            bool currentlyRight = anim.Dir > 0;
            bool wantRight = (newDir == Direction.Right);
            if (currentlyRight != wantRight)
            {
                ch.Keys[(int)InputType.Left].Held = false;
                ch.Keys[(int)InputType.Right].Held = false;
                flipInProgress = true;
                blockFlip = false;
                try { anim.Flip(); } catch { }
                blockFlip = true;
                flipInProgress = false;
                flipFreezeFrames = freezeDurations[freezeMode];
                flipCooldown = 5;
            }
            lockedDirection = newDir;
            anim.TargetDir = lockedDirection;
        }

        private static Vector2 GetHeadPosition()
        {
            var character = Character.Controlled;
            if (character == null) return Vector2.Zero;
            try
            {
                if (character.AnimController?.Limbs != null)
                    foreach (var limb in character.AnimController.Limbs)
                        if (limb.type == LimbType.Head && limb.body != null)
                            return limb.WorldPosition;
            }
            catch { }
            return character.WorldPosition;
        }

        private static Vector2 GetCameraPosition() => cameraFollowMode ? GetHeadPosition() : GetHeadPosition() + camOffset;
        
        private static bool IsLookingRight()
        {
            float deg = MathHelper.ToDegrees(camYaw);
            if (deg < 0) deg += 360f;
            return !(deg > 90f && deg < 270f);
        }
        
        private static bool IsLookingUp() => camPitch > LOOK_UP_THRESHOLD;
        private static bool IsLookingDown() => camPitch < LOOK_DOWN_THRESHOLD;
        private static bool IsLookingStraight() => !IsLookingUp() && !IsLookingDown();

        private static bool IsNearLadder(Character ch)
        {
            if (ch == null) return false;
            try
            {
                if (ch.AnimController?.IsClimbing == true) return true;
                foreach (var ladder in Ladder.List)
                {
                    if (ladder?.Item == null) continue;
                    float dx = Math.Abs(ch.WorldPosition.X - ladder.Item.WorldPosition.X);
                    float ladderTop = ladder.Item.WorldRect.Y;
                    float ladderBottom = ladder.Item.WorldRect.Y - ladder.Item.WorldRect.Height;
                    if (dx < LADDER_INTERACT_DISTANCE && ch.WorldPosition.Y >= ladderBottom - 50f && ch.WorldPosition.Y <= ladderTop + 50f)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static Ladder FindNearestLadder(Character ch)
        {
            if (ch == null) return null;
            Ladder nearest = null;
            float minDist = float.MaxValue;
            try
            {
                foreach (var ladder in Ladder.List)
                {
                    if (ladder?.Item == null) continue;
                    float dx = Math.Abs(ch.WorldPosition.X - ladder.Item.WorldPosition.X);
                    float ladderTop = ladder.Item.WorldRect.Y;
                    float ladderBottom = ladder.Item.WorldRect.Y - ladder.Item.WorldRect.Height;
                    if (dx < LADDER_INTERACT_DISTANCE && ch.WorldPosition.Y >= ladderBottom - 50f && ch.WorldPosition.Y <= ladderTop + 50f && dx < minDist)
                    {
                        minDist = dx;
                        nearest = ladder;
                    }
                }
            }
            catch { }
            return nearest;
        }

        private static bool IsClimbing(Character ch)
        {
            try { return ch?.AnimController?.IsClimbing ?? false; }
            catch { return false; }
        }

        private static void GetCameraVectors(out Vector3 cameraPos, out Vector3 forward, out Vector3 up)
        {
            Vector2 headPos = GetCameraPosition();
            cameraPos = new Vector3(headPos.X, headPos.Y, 0f);
            float cosYaw = MathF.Cos(camYaw), sinYaw = MathF.Sin(camYaw);
            float cosPitch = MathF.Cos(camPitch), sinPitch = MathF.Sin(camPitch);
            forward = new Vector3(cosYaw * cosPitch, sinPitch, sinYaw * cosPitch);
            up = new Vector3(0, 1, 0);
        }

        private static Matrix BuildViewProjection()
        {
            GetCameraVectors(out Vector3 cameraPos, out Vector3 forward, out Vector3 up);
            Matrix view = Matrix.CreateLookAt(cameraPos, cameraPos + forward * 100f, up);
            Matrix projection = Matrix.CreatePerspectiveFieldOfView(fov, (float)GameMain.GraphicsWidth / GameMain.GraphicsHeight, 1f, 50000f);
            return view * projection;
        }

        private static Matrix GetScreenMatrix()
        {
            int sw = GameMain.GraphicsWidth, sh = GameMain.GraphicsHeight;
            return Matrix.CreateScale(sw / 2f, -sh / 2f, 1f) * Matrix.CreateTranslation(sw / 2f, sh / 2f, 0f);
        }

        private static Matrix BuildWallMatrix(float z) => Matrix.CreateScale(1, -1, 1) * Matrix.CreateTranslation(0, 0, z) * BuildViewProjection() * GetScreenMatrix();
        private static Matrix BuildFloorMatrix(float y) => Matrix.CreateScale(1, -1, 1) * Matrix.CreateRotationX(-MathF.PI / 2f) * Matrix.CreateTranslation(0, y, 0) * BuildViewProjection() * GetScreenMatrix();
        private static Matrix BuildSideMatrix(float x) => Matrix.CreateScale(1, -1, 1) * Matrix.CreateRotationY(MathF.PI / 2f) * Matrix.CreateTranslation(x, 0, 0) * BuildViewProjection() * GetScreenMatrix();

        private static void FindCurrentRoom(Vector2 pos)
        {
            roomLeft = pos.X - 400f; roomRight = pos.X + 400f;
            roomFloor = pos.Y - 200f; roomCeiling = pos.Y + 200f;
            currentHull = null;
            foreach (var hull in Hull.HullList)
            {
                try
                {
                    if (hull.Submarine == null) continue;
                    var wr = hull.WorldRect;
                    if (pos.X >= wr.X && pos.X <= wr.X + wr.Width && pos.Y <= wr.Y && pos.Y >= wr.Y - wr.Height)
                    {
                        roomLeft = wr.X; roomRight = wr.X + wr.Width;
                        roomFloor = wr.Y - wr.Height; roomCeiling = wr.Y;
                        currentHull = hull;
                        break;
                    }
                }
                catch { }
            }
        }

        
        private static void DrawAngledStructures(Vector2 camPos, Camera cam)
        {
            if (angledStructures == null || angledStructures.Count == 0) return;

            void DrawNormalPass(float z)
            {
                fpBatch.Begin(
                    SpriteSortMode.BackToFront,
                    BlendState.NonPremultiplied,
                    SamplerState.LinearWrap,
                    depthWriteState,
                    RasterizerState.CullNone,
                    null,
                    BuildWallMatrix(z));

                foreach (var st in angledStructures)
                {
                    try
                    {
                        if (IsSpecialAngledShell(st)) continue;

                        st.Draw(fpBatch, false, true);
                        st.Draw(fpBatch, false, false);
                    }
                    catch { }
                }

                fpBatch.End();
            }

            // Обычные angled
            DrawNormalPass(-layerDepth);
            DrawNormalPass(+layerDepth);

            // Special shell: alpha-extruded
            foreach (var st in angledStructures)
            {
                try
                {
                    if (!IsSpecialAngledShell(st)) continue;

                    var s = GetAngledModeSettings();

                    float thickness = 300f;
                    bool mirrorX = ResolveSpecialShellMirrorX(st, s.mirrorMode);

                    // Локальный override прозрачности для конкретного стекла
                    byte alphaTh = s.alphaTh;
                    string sid = "";
                    try { sid = st.Prefab?.Identifier.ToString().ToLowerInvariant() ?? ""; } catch { }

                    if (sid.Contains("sshellaglassf"))
                    {
                        alphaTh = 110;
                    }

                    DrawSpecialShellExtruded(
                        st,
                        thickness,
                        alphaTh,
                        s.drawCaps,
                        s.drawSides,
                        mirrorX,
                        s.uvInsetPx,
                        s.opaqueSides);
                }
                catch { }
            }
        }

        private static void DrawAngledStructureSides()
        {
            if (angledStructures == null || angledStructures.Count == 0) return;

            var gd = GameMain.Instance.GraphicsDevice;

            SetupBasicEffect3D();
            basicEffect.World = Matrix.Identity;
            basicEffect.Texture = pixelTexture;

            gd.DepthStencilState = DepthStencilState.Default;
            gd.BlendState = BlendState.NonPremultiplied;
            gd.RasterizerState = RasterizerState.CullNone;
            gd.SamplerStates[0] = SamplerState.PointClamp;

            float zBack = -layerDepth;
            float zFront = +layerDepth;

            foreach (var st in angledStructures)
            {
                try
                {
                    // Special shell уже рисуются в DrawAngledStructures (caps + bridges)
                    if (IsSpecialAngledShell(st)) continue;

                    var wr = st.WorldRect;
                    if (wr.Width <= 0 || wr.Height <= 0) continue;

                    float cx = wr.X + wr.Width * 0.5f;
                    float cy = wr.Y - wr.Height * 0.5f;

                    float rot = (st.FlippedX != st.FlippedY) ? st.RotationRad : -st.RotationRad;

                    float hw = wr.Width * 0.5f;
                    float hh = wr.Height * 0.5f;

                    Vector2 Rotate(float lx, float ly)
                    {
                        float c = MathF.Cos(rot);
                        float s = MathF.Sin(rot);
                        return new Vector2(cx + lx * c - ly * s, cy + lx * s + ly * c);
                    }

                    Vector2 tl = Rotate(-hw, +hh);
                    Vector2 tr = Rotate(+hw, +hh);
                    Vector2 bl = Rotate(-hw, -hh);
                    Vector2 br = Rotate(+hw, -hh);

                    Color cSide = Mult(st.SpriteColor, 0.78f);
                    Color cTop = Mult(st.SpriteColor, 0.92f);
                    Color cBottom = Mult(st.SpriteColor, 0.60f);

                    DrawColoredQuad(gd, new Vector3(tl.X, tl.Y, zBack), new Vector3(tl.X, tl.Y, zFront), new Vector3(bl.X, bl.Y, zBack), new Vector3(bl.X, bl.Y, zFront), cSide);
                    DrawColoredQuad(gd, new Vector3(tr.X, tr.Y, zFront), new Vector3(tr.X, tr.Y, zBack), new Vector3(br.X, br.Y, zFront), new Vector3(br.X, br.Y, zBack), cSide);

                    DrawColoredQuad(gd, new Vector3(tl.X, tl.Y, zBack), new Vector3(tr.X, tr.Y, zBack), new Vector3(tl.X, tl.Y, zFront), new Vector3(tr.X, tr.Y, zFront), cTop);
                    DrawColoredQuad(gd, new Vector3(bl.X, bl.Y, zFront), new Vector3(br.X, br.Y, zFront), new Vector3(bl.X, bl.Y, zBack), new Vector3(br.X, br.Y, zBack), cBottom);
                }
                catch { }
            }

            basicEffect.World = Matrix.Identity;
        }	
		
		
		
		
		
		
        private static void CacheStructures(Vector2 camPos)
        {
            horizontalStructures.Clear();
            verticalStructures.Clear();
            stairStructures.Clear();
            doorItems.Clear();
            angledStructures.Clear();
        
            foreach (var entity in MapEntity.MapEntityList)
            {
                try
                {
                    if (entity is Structure st)
                    {
                        if (st.Submarine == null || st.Removed) continue;
        
                        var wr = st.WorldRect;
                        float dist = Vector2.Distance(camPos, new Vector2(wr.X + wr.Width / 2f, wr.Y - wr.Height / 2f));
        
                        if (xrayMode)
                        {
                            if (dist > 10000f) continue;
                        }
                        else
                        {
                            if (st.Submarine != Character.Controlled?.Submarine || dist > 2000f) continue;
                        }
        
                        if (st.StairDirection != Direction.None)
                        {
                            stairStructures.Add(st);
                            continue;
                        }
        
                        if (IsSpecialAngledShell(st))
                        {
                            angledStructures.Add(st);
                            continue;
                        }

                        if (!st.HasBody) continue;

                        if (IsAngledStructure(st))
                        {
                            angledStructures.Add(st);
                            continue;
                        }

                        if (IsSpecialAngledShell(st))
                        {
                            angledStructures.Add(st);
                            continue;
                        }
        
                        if (IsSolidFloorCeiling(st)) horizontalStructures.Add(st);
                        else if (IsSolidWall(st)) verticalStructures.Add(st);
                    }
                }
                catch { }
            }
        
            foreach (var item in Item.ItemList)
            {
                try
                {
                    if (item == null || item.Submarine == null || item.Removed) continue;
                    var door = item.GetComponent<Door>();
                    if (door == null) continue;
        
                    var wr = item.WorldRect;
                    float dist = Vector2.Distance(camPos, new Vector2(wr.X + wr.Width / 2f, wr.Y - wr.Height / 2f));
        
                    if (xrayMode)
                    {
                        if (dist > 10000f) continue;
                    }
                    else
                    {
                        if (item.Submarine != Character.Controlled?.Submarine || dist > 2000f) continue;
                    }
        
                    doorItems.Add(item);
                }
                catch { }
            }
        }

        private static void GetWallClipAmounts(Structure wallSt, out float clipTop, out float clipBottom)
        {
            clipTop = 0f; clipBottom = 0f;
            var wallRect = wallSt.WorldRect;
            float wallLeft = wallRect.X, wallRight = wallRect.X + wallRect.Width;
            float wallTop = wallRect.Y, wallBottom = wallRect.Y - wallRect.Height;
            foreach (var floorSt in horizontalStructures)
            {
                try
                {
                    var floorRect = floorSt.WorldRect;
                    float floorLeft = floorRect.X, floorRight = floorRect.X + floorRect.Width;
                    float floorTop = floorRect.Y, floorBottom = floorRect.Y - floorRect.Height;
                    if ((wallRight > floorLeft) && (wallLeft < floorRight) && (wallTop > floorBottom) && (wallBottom < floorTop))
                    {
                        if (wallTop > floorBottom && wallTop <= floorTop) clipTop = MathF.Max(clipTop, wallTop - floorBottom);
                        if (wallBottom >= floorBottom && wallBottom < floorTop) clipBottom = MathF.Max(clipBottom, floorTop - wallBottom);
                    }
                }
                catch { }
            }
        }

        private static bool IsSolidFloorCeiling(Structure st)
        {
            if (st == null || !st.HasBody || st.Submarine == null || !st.IsHorizontal) return false;
            if (st.StairDirection != Direction.None) return false;
            string id = "";
            try { if (st.Prefab != null) id = st.Prefab.Identifier.ToString().ToLowerInvariant(); } catch { }
            if (id.Contains("platform") || id.Contains("stair") || id.Contains("ladder") || id.Contains("hatch") || id.Contains("grate") || id.Contains("catwalk")) return false;
            try { if (!st.CastShadow) return false; } catch { }
            return true;
        }

        private static bool IsSolidWall(Structure st)
        {
            if (st == null || !st.HasBody || st.Submarine == null || st.IsHorizontal) return false;
            if (st.StairDirection != Direction.None) return false;
            try { if (!st.CastShadow) return false; } catch { }
            return true;
        }

        private static bool GetTexture(Structure st, out Texture2D tex, out Rectangle src, out float scale)
        {
            tex = null; src = Rectangle.Empty; scale = 1f;
            try
            {
                if (st.Sprite == null) return false;
                tex = st.Sprite.Texture;
                if (tex == null || tex.IsDisposed) return false;
                src = st.Sprite.SourceRect;
                if (src.Width <= 0 || src.Height <= 0) return false;
                scale = st.Scale;
                if (scale <= 0.01f) scale = 1f;
                return true;
            }
            catch { return false; }
        }

        private static bool GetDoorSpriteData(Item item, Door door, out Texture2D tex, out Rectangle src, out Color color)
        {
            tex = null; src = Rectangle.Empty; color = Color.White;
            try
            {
                Sprite sprite = null;
                if (doorSpriteField != null) sprite = doorSpriteField.GetValue(door) as Sprite;
                if (sprite == null || sprite.Texture == null || sprite.Texture.IsDisposed) sprite = item.Sprite;
                if (sprite == null) return false;
                tex = sprite.Texture;
                if (tex == null || tex.IsDisposed) return false;
                src = sprite.SourceRect;
                if (src.Width <= 0 || src.Height <= 0) return false;
                color = item.SpriteColor;
                return true;
            }
            catch { return false; }
        }

        private static void DrawTiled(Structure st, Rectangle dest, Color tint)
        {
            if (dest.Width <= 0 || dest.Height <= 0) return;

            if (GetTexture(st, out var tex, out var src, out float scale))
            {
                DrawWallSurface(
                    fpBatch,
                    tex,
                    src,
                    dest,
                    tint,
                    scale,
                    useBottomStrip: false,
                    phaseX: dest.X,
                    phaseY: dest.Y);
            }
            else
            {
                fpBatch.Draw(pixelTexture, dest, tint);
            }
        }


        private static bool ShouldDrawEntity(MapEntity entity, Vector2 camPos)
        {
            try
            {
                var wr = entity.WorldRect;
                float cx = wr.X + wr.Width / 2f, cy = wr.Y - wr.Height / 2f;
                if (xrayMode) return entity.Submarine != null && Vector2.Distance(camPos, new Vector2(cx, cy)) <= 10000f;
                return cx >= roomLeft - 100f && cx <= roomRight + 100f && cy >= roomFloor - 100f && cy <= roomCeiling + 100f && Vector2.Distance(camPos, new Vector2(cx, cy)) <= 5000f;
            }
            catch { return false; }
        }

        private static void DrawBackLayers(Vector2 camPos)
        {
            foreach (float z in new[] { -layerDepth, layerDepth })
            {
                fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, SamplerState.LinearWrap, null, RasterizerState.CullNone, null, BuildWallMatrix(z));
                foreach (var entity in MapEntity.MapEntityList)
                {
                    try
                    {
                        if (entity is Structure st && st.Submarine != null)
                        {
                            var wr = st.WorldRect;
                            if (Vector2.Distance(camPos, new Vector2(wr.X + wr.Width/2f, wr.Y - wr.Height/2f)) <= 5000f)
                            { st.Draw(fpBatch, false, true); st.Draw(fpBatch, false, false); }
                        }
                    }
                    catch { }
                }
                fpBatch.End();
            }
        }

        private static void DrawBackground(Vector2 camPos)
        {
            Color bgColor = new Color(10, 15, 22);
            try { if (Level.Loaded != null) bgColor = Level.Loaded.BackgroundColor; } catch { }
            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, null, null, RasterizerState.CullNone, null, BuildWallMatrix(-layerDepth * 5f));
            fpBatch.Draw(pixelTexture, new Rectangle((int)camPos.X - 50000, -(int)camPos.Y - 50000, 100000, 100000), bgColor);
            fpBatch.End();
            if (bgEnabled) DrawBackLayers(camPos);
        }



        private static void DrawWallEntities(Vector2 camPos, float z, Camera cam)
        {
            fpBatch.Begin(
                SpriteSortMode.BackToFront,
                BlendState.NonPremultiplied,
                SamplerState.LinearWrap,
                null,
                RasterizerState.CullNone,
                null,
                BuildWallMatrix(z));

            foreach (var entity in MapEntity.MapEntityList)
            {
                try
                {
                    if (entity is Structure st && st.HasBody && st.Submarine != null && ShouldDrawEntity(st, camPos))
                    {
                        // КЛЮЧЕВОЙ ФИКС:
                        // angled/special shell части НЕ рисуем здесь,
                        // они идут отдельным angled-проходом
                        if (IsAngledStructure(st) || IsSpecialAngledShell(st))
                        {
                            continue;
                        }

                        st.Draw(fpBatch, false, true);
                        st.Draw(fpBatch, false, false);
                    }
                    else if (entity is Item item &&
                             (item.ParentInventory == null || item.ParentInventory is CharacterInventory) &&
                             ShouldDrawEntity(item, camPos))
                    {
                        item.Draw(fpBatch, false, true);
                        item.Draw(fpBatch, false, false);
                    }
                }
                catch { }
            }

            fpBatch.End();
        }



        private static void DrawSideWallStructures(Vector2 camPos)
        {
            int zStart = (int)(-layerDepth), zLen = (int)(layerDepth * 2f);
            bool sameBrightness = (brightnessMode == 1);
            foreach (var st in verticalStructures)
            {
                try
                {
                    var wr = st.WorldRect;
                    float stLeft = wr.X, stRight = wr.X + wr.Width, stTop = wr.Y, stBottom = wr.Y - wr.Height;
                    int thickness = Math.Max((int)(stRight - stLeft), 10);
                    float clippedTop = stTop, clippedBottom = stBottom;
                    if (!xrayMode)
                    {
                        float tolerance = 80f;
                        if (MathF.Abs((stLeft + stRight) / 2f - roomLeft) >= tolerance && MathF.Abs((stLeft + stRight) / 2f - roomRight) >= tolerance) continue;
                        if (stTop < roomFloor || stBottom > roomCeiling) continue;
                        clippedTop = MathF.Min(stTop, roomCeiling);
                        clippedBottom = MathF.Max(stBottom, roomFloor);
                    }
                    GetWallClipAmounts(st, out float clipTop, out float clipBottom);
                    clippedTop -= clipTop; clippedBottom += clipBottom;
                    int wallHeight = (int)(clippedTop - clippedBottom);
                    if (wallHeight <= 0) continue;
                    float m = sameBrightness ? 0.7f : 0.55f, d = sameBrightness ? 0.7f : 0.35f, vd = sameBrightness ? 0.7f : 0.25f;
                    Color tint = new Color((int)(st.SpriteColor.R * m), (int)(st.SpriteColor.G * m), (int)(st.SpriteColor.B * m));
                    Color tintD = new Color((int)(st.SpriteColor.R * d), (int)(st.SpriteColor.G * d), (int)(st.SpriteColor.B * d));
                    Color tintVD = new Color((int)(st.SpriteColor.R * vd), (int)(st.SpriteColor.G * vd), (int)(st.SpriteColor.B * vd));
                    
                    fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildSideMatrix(stLeft));
                    DrawTiled(st, new Rectangle(zStart, -(int)clippedTop, zLen, wallHeight), tint);
                    fpBatch.End();
                    fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildSideMatrix(stRight));
                    DrawTiled(st, new Rectangle(zStart, -(int)clippedTop, zLen, wallHeight), sameBrightness ? tint : tintVD);
                    fpBatch.End();
                    fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildFloorMatrix(clippedTop));
                    DrawTiled(st, new Rectangle((int)stLeft, zStart, thickness, zLen), sameBrightness ? tint : tintD);
                    fpBatch.End();
                    fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildFloorMatrix(clippedBottom));
                    DrawTiled(st, new Rectangle((int)stLeft, zStart, thickness, zLen), sameBrightness ? tint : tintD);
                    fpBatch.End();
                }
                catch { }
            }
        }

        
      

        
        
        
        
       
        private static void DrawFloorCeilingStructures(Vector2 camPos)
        {
            int zStart = (int)(-layerDepth), zLen = (int)(layerDepth * 2f);
            float tolerance = 70f;
            bool sameBrightness = (brightnessMode == 1);
            bool drawTopBottomCaps = xrayMode; // вне xray убираем длинные верх/низ плоскости

            bool drewFloor = false;
            bool drewCeiling = false;

            Structure floorRef = null;
            Structure ceilRef = null;

            foreach (var st in horizontalStructures)
            {
                try
                {
                    var wr = st.WorldRect;
                    float stTop = wr.Y, stBottom = wr.Y - wr.Height, stLeft = wr.X, stRight = wr.X + wr.Width;

                    if (!xrayMode &&
                        (stRight < roomLeft - 500f || stLeft > roomRight + 500f || stTop < roomFloor - 500f || stBottom > roomCeiling + 500f))
                    {
                        continue;
                    }

                    float dFloor = Math.Min(MathF.Abs(stBottom - roomFloor), MathF.Abs(stTop - roomFloor));
                    float dCeil = Math.Min(MathF.Abs(stTop - roomCeiling), MathF.Abs(stBottom - roomCeiling));

                    bool isFloor = false, isCeiling = false;

                    if (xrayMode)
                    {
                        foreach (var hull in Hull.HullList)
                        {
                            try
                            {
                                if (hull.Submarine == null) continue;
                                var hr = hull.WorldRect;
                                if (stLeft < hr.X + hr.Width + 50f && stRight > hr.X - 50f)
                                {
                                    float hTop = hr.Y;
                                    float hBottom = hr.Y - hr.Height;

                                    float dF = Math.Min(MathF.Abs(stBottom - hBottom), MathF.Abs(stTop - hBottom));
                                    float dC = Math.Min(MathF.Abs(stTop - hTop), MathF.Abs(stBottom - hTop));

                                    if (dF < tolerance) isFloor = true;
                                    if (dC < tolerance) isCeiling = true;
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        if (dFloor < tolerance) isFloor = true;
                        if (dCeil < tolerance) isCeiling = true;
                    }

                    if (!isFloor && !isCeiling) continue;

                    if (isFloor && floorRef == null) floorRef = st;
                    if (isCeiling && ceilRef == null) ceilRef = st;

                    int drawWidth = (int)(stRight - stLeft);
                    if (drawWidth <= 0) continue;

                    float fm = sameBrightness ? 0.8f : 0.7f, fd = sameBrightness ? 0.8f : 0.4f;
                    Color tint = new Color((int)(st.SpriteColor.R * fm), (int)(st.SpriteColor.G * fm), (int)(st.SpriteColor.B * fm));
                    Color tintD = new Color((int)(st.SpriteColor.R * fd), (int)(st.SpriteColor.G * fd), (int)(st.SpriteColor.B * fd));

                    int thickness = Math.Max((int)wr.Height, 15);
                    int height = Math.Max((int)(stTop - stBottom), thickness);

                    if (drawTopBottomCaps)
                    {
                        // Верхняя крышка структуры
                        fpBatch.Begin(
                            SpriteSortMode.Deferred,
                            BlendState.Opaque,
                            SamplerState.LinearWrap,
                            depthWriteState,
                            RasterizerState.CullNone,
                            null,
                            BuildFloorMatrix(stTop));

                        if (GetTexture(st, out var texTop, out var srcTop, out float scTop))
                        {
                            DrawTiledContinuous(
                                fpBatch,
                                texTop,
                                srcTop,
                                new Rectangle((int)stLeft, zStart, drawWidth, zLen),
                                tint,
                                Math.Max(scTop, 0.01f),
                                (int)stLeft,
                                0);
                        }
                        else
                        {
                            fpBatch.Draw(pixelTexture, new Rectangle((int)stLeft, zStart, drawWidth, zLen), tint);
                        }

                        fpBatch.End();

                        // Нижняя крышка структуры (режимы через floorBottomMode)
                        fpBatch.Begin(
                            SpriteSortMode.Deferred,
                            BlendState.Opaque,
                            SamplerState.LinearWrap,
                            depthWriteState,
                            RasterizerState.CullNone,
                            null,
                            BuildFloorMatrix(stBottom));

                        DrawBottomCapByMode(
                            st,
                            new Rectangle((int)stLeft, zStart, drawWidth, zLen),
                            tint);

                        fpBatch.End();
                    }

                    // Левый торец
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.Opaque,
                        SamplerState.LinearWrap,
                        depthWriteState,
                        RasterizerState.CullNone,
                        null,
                        BuildSideMatrix(stLeft));

                    if (GetTexture(st, out var texL, out var srcL, out float scL))
                    {
                        DrawTiledContinuous(
                            fpBatch,
                            texL,
                            srcL,
                            new Rectangle(zStart, -(int)stTop, zLen, height),
                            sameBrightness ? tint : tintD,
                            Math.Max(scL, 0.01f),
                            zStart,
                            -(int)stTop);
                    }
                    else
                    {
                        fpBatch.Draw(pixelTexture, new Rectangle(zStart, -(int)stTop, zLen, height), sameBrightness ? tint : tintD);
                    }

                    fpBatch.End();

                    // Правый торец
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.Opaque,
                        SamplerState.LinearWrap,
                        depthWriteState,
                        RasterizerState.CullNone,
                        null,
                        BuildSideMatrix(stRight));

                    if (GetTexture(st, out var texR, out var srcR, out float scR))
                    {
                        DrawTiledContinuous(
                            fpBatch,
                            texR,
                            srcR,
                            new Rectangle(zStart, -(int)stTop, zLen, height),
                            sameBrightness ? tint : tintD,
                            Math.Max(scR, 0.01f),
                            zStart,
                            -(int)stTop);
                    }
                    else
                    {
                        fpBatch.Draw(pixelTexture, new Rectangle(zStart, -(int)stTop, zLen, height), sameBrightness ? tint : tintD);
                    }

                    fpBatch.End();

                    if (isFloor) drewFloor = true;
                    if (isCeiling) drewCeiling = true;
                }
                catch { }
            }

            DrawHullBoundaryTexturedFallback(floorRef, ceilRef, !drewFloor, !drewCeiling);
        }


        private static void DrawStairStructures(Vector2 camPos)
        {
            if (stairRenderMode == 0 || stairStructures.Count == 0) return;
            int zStart = (int)(-layerDepth), zLen = (int)(layerDepth * 2f);
            
            foreach (var st in stairStructures)
            {
                try
                {
                    var wr = st.WorldRect;
                    float stLeft = wr.X, stRight = wr.X + wr.Width;
                    float stTop = wr.Y, stBottom = wr.Y - wr.Height;
                    float stWidth = stRight - stLeft;
                    float stHeight = stTop - stBottom;
                    if (stWidth <= 0 || stHeight <= 0) continue;
                    
                    bool goesRight = (st.StairDirection == Direction.Right);
                    const float TOP_OFFSET = 25f;
                    float adjustedTop = stTop - TOP_OFFSET;
                    
                    Color stepColor = st.SpriteColor;
                    if (stepColor.R < 20 && stepColor.G < 20 && stepColor.B < 20)
                        stepColor = new Color(120, 110, 100);
                    
                    int slices = Math.Max((int)(stWidth / 12f), 10);
                    float sliceWidth = stWidth / slices;
                    
                    for (int i = 0; i < slices; i++)
                    {
                        float t = (i + 0.5f) / slices;
                        float sliceY = goesRight ? MathHelper.Lerp(stBottom, adjustedTop, t) : MathHelper.Lerp(adjustedTop, stBottom, t);
                        float sliceX = stLeft + i * sliceWidth;
                        int sW = Math.Max((int)sliceWidth + 2, 4);
                        
                        float brightness = 0.4f + 0.3f * t;
                        Color sliceTint = new Color((int)(stepColor.R * brightness), (int)(stepColor.G * brightness), (int)(stepColor.B * brightness));
                        
                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp, depthWriteState, RasterizerState.CullNone, null, BuildFloorMatrix(sliceY));
                        fpBatch.Draw(pixelTexture, new Rectangle((int)sliceX, zStart, sW, zLen), sliceTint);
                        fpBatch.End();
                    }
                }
                catch { }
            }
        }

        private static Color Mult(Color c, float m)
        {
            return new Color(
                (int)Math.Min(c.R * m, 255),
                (int)Math.Min(c.G * m, 255),
                (int)Math.Min(c.B * m, 255),
                c.A);
        }

        private static (float x, float y, float z, bool useWindowMode) GetDoorScaleFull(Item item)
        {
            if (item?.Prefab != null)
            {
                string id = item.Prefab.Identifier.ToString().ToLowerInvariant();
                if (customDoorScales.TryGetValue(id, out var scale))
                    return scale;
            }
            return (DOOR_SCALE_X, DOOR_SCALE_Y, DOOR_SCALE_Z, false);
        }

        private static void UpdateNearbyDoors(Vector2 camPos)
        {
            nearbyDoorsList.Clear();
            foreach (var item in doorItems)
            {
                try
                {
                    if (item?.Prefab == null) continue;
                    var wr = item.WorldRect;
                    float dist = Vector2.Distance(camPos, new Vector2(wr.X + wr.Width / 2f, wr.Y - wr.Height / 2f));
                    string id = item.Prefab.Identifier.ToString();
                    string name = item.Name ?? "Unknown";
                    nearbyDoorsList.Add((id, name, dist));
                }
                catch { }
            }
            nearbyDoorsList = nearbyDoorsList.OrderBy(d => d.dist).Take(10).ToList();
        }

        private static void DrawDoorStructures(Vector2 camPos)
        {
            if (doorRenderMode == 0 || doorItems.Count == 0) return;
            int zStart = (int)(-layerDepth), zLen = (int)(layerDepth * 2f);
            
            foreach (var item in doorItems)
            {
                try
                {
                    var door = item.GetComponent<Door>();
                    if (door == null) continue;
                    
                    var wr = item.WorldRect;
                    float doorLeft = wr.X, doorRight = wr.X + wr.Width;
                    float doorTop = wr.Y, doorBottom = wr.Y - wr.Height;
                    float doorWidth = doorRight - doorLeft;
                    float doorHeight = doorTop - doorBottom;
                    if (doorWidth <= 0 || doorHeight <= 0) continue;
                    
                    float openState = door.OpenState;
                    
                    bool hasTexture = GetDoorSpriteData(item, door, out var tex, out var src, out var baseColor);
                    
                    float sf = 1f - openState * 0.95f;
                    if (sf < 0.05f) continue;

                    bool ok = hasTexture && tex != null && !tex.IsDisposed;

                    Color cF = baseColor;
                    Color cB = Mult(baseColor, 0.65f);
                    Color cS = Mult(baseColor, 0.8f);
                    Color cT = Mult(baseColor, 0.9f);
                    Color cBot = Mult(baseColor, 0.5f);

                    if (baseColor.R < 10 && baseColor.G < 10 && baseColor.B < 10)
                    {
                        cF = new Color(120, 120, 120);
                        cB = new Color(80, 80, 80);
                        cS = new Color(100, 100, 100);
                        cT = new Color(110, 110, 110);
                        cBot = new Color(60, 60, 60);
                    }

                    var (scaleX, scaleY, scaleZ, useWindowMode) = GetDoorScaleFull(item);

                    float centerX = (doorLeft + doorRight) / 2f;
                    float centerY = (doorTop + doorBottom) / 2f;

                    float baseW = doorWidth * scaleX;
                    float baseH = doorHeight * scaleY;
                    float baseZLen = layerDepth * 2f * scaleZ;

                    string doorId = item?.Prefab?.Identifier.ToString() ?? "";

                    float newL = centerX - baseW / 2f;
                    float newR = centerX + baseW / 2f;
                    float newT = centerY + baseH / 2f;
                    float newB = centerY - baseH / 2f;
                    int scaledZLen = (int)Math.Min(baseZLen, layerDepth * 2f);
                    int scaledZStart = -scaledZLen / 2;

                    float dH = baseH * sf;
                    if (dH < 1) continue;

                    bool isWindowedDoor = doorId.ToLowerInvariant() == "windoweddoorwbuttons";
                    bool isHatchDoor = doorId.ToLowerInvariant() == "hatchwbuttons";

                    float dT = newT;
                    float dB = newT - dH;
                    int w = (int)baseW;
                    int h = (int)dH;

                    float drawL = newL;
                    float drawR = newR;
                    int drawW = w;
                    int drawH = h;

                    if (isHatchDoor)
                    {
                        drawW = (int)(baseW * sf);
                        drawH = (int)baseH;
                        drawL = newL;
                        drawR = newL + drawW;
                        dT = newT;
                        dB = newT - baseH;
                        h = drawH;
                        w = drawW;
                    }

                    float zFront = scaledZStart + scaledZLen;
                    float zBack = scaledZStart;

                    float sinYaw = MathF.Sin(camYaw);
                    float cosYaw = MathF.Cos(camYaw);

                    if (sinYaw > 0)
                    {
                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildWallMatrix(zBack));
                        if (ok) fpBatch.Draw(tex, new Rectangle((int)drawL, -(int)dT, w, h), src, cB);
                        else fpBatch.Draw(pixelTexture, new Rectangle((int)drawL, -(int)dT, w, h), cB);
                        fpBatch.End();

                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildWallMatrix(zFront));
                        if (ok) fpBatch.Draw(tex, new Rectangle((int)drawL, -(int)dT, w, h), src, cF);
                        else fpBatch.Draw(pixelTexture, new Rectangle((int)drawL, -(int)dT, w, h), cF);
                        fpBatch.End();
                    }
                    else
                    {
                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildWallMatrix(zFront));
                        if (ok) fpBatch.Draw(tex, new Rectangle((int)drawL, -(int)dT, w, h), src, cF);
                        else fpBatch.Draw(pixelTexture, new Rectangle((int)drawL, -(int)dT, w, h), cF);
                        fpBatch.End();

                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildWallMatrix(zBack));
                        if (ok) fpBatch.Draw(tex, new Rectangle((int)drawL, -(int)dT, w, h), src, cB);
                        else fpBatch.Draw(pixelTexture, new Rectangle((int)drawL, -(int)dT, w, h), cB);
                        fpBatch.End();
                    }

                    if (!isWindowedDoor)
                    {
                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildFloorMatrix(dT));
                        if (ok) fpBatch.Draw(tex, new Rectangle((int)drawL, scaledZStart, w, scaledZLen), src, cT);
                        else fpBatch.Draw(pixelTexture, new Rectangle((int)drawL, scaledZStart, w, scaledZLen), cT);
                        fpBatch.End();

                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildFloorMatrix(dB));
                        if (ok) fpBatch.Draw(tex, new Rectangle((int)drawL, scaledZStart, w, scaledZLen), src, cBot);
                        else fpBatch.Draw(pixelTexture, new Rectangle((int)drawL, scaledZStart, w, scaledZLen), cBot);
                        fpBatch.End();
                    }

                    if (cosYaw > 0)
                    {
                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildSideMatrix(drawR));
                        if (ok) fpBatch.Draw(tex, new Rectangle(scaledZStart, -(int)dT, scaledZLen, h), src, cS);
                        else fpBatch.Draw(pixelTexture, new Rectangle(scaledZStart, -(int)dT, scaledZLen, h), cS);
                        fpBatch.End();

                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildSideMatrix(drawL));
                        if (ok) fpBatch.Draw(tex, new Rectangle(scaledZStart, -(int)dT, scaledZLen, h), src, cS);
                        else fpBatch.Draw(pixelTexture, new Rectangle(scaledZStart, -(int)dT, scaledZLen, h), cS);
                        fpBatch.End();
                    }
                    else
                    {
                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildSideMatrix(drawL));
                        if (ok) fpBatch.Draw(tex, new Rectangle(scaledZStart, -(int)dT, scaledZLen, h), src, cS);
                        else fpBatch.Draw(pixelTexture, new Rectangle(scaledZStart, -(int)dT, scaledZLen, h), cS);
                        fpBatch.End();

                        fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearWrap, depthWriteState, RasterizerState.CullNone, null, BuildSideMatrix(drawR));
                        if (ok) fpBatch.Draw(tex, new Rectangle(scaledZStart, -(int)dT, scaledZLen, h), src, cS);
                        else fpBatch.Draw(pixelTexture, new Rectangle(scaledZStart, -(int)dT, scaledZLen, h), cS);
                        fpBatch.End();
                    }
                }
                catch { }
            }
        }




        private static void DrawWater(Vector2 camPos)
        {
            EnsureDepthStatesInitialized();

            var ch = Character.Controlled;
            bool controlledInWater = false;
            try
            {
                controlledInWater = ch != null && (ch.InWater || ch.AnimController?.InWater == true);
            }
            catch { }

            // Легкий экранный tint, когда игрок под водой
            if (controlledInWater)
            {
                fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);
                fpBatch.Draw(
                    pixelTexture,
                    new Rectangle(0, 0, GameMain.GraphicsWidth, GameMain.GraphicsHeight),
                    new Color((byte)24, (byte)78, (byte)120, (byte)36));
                fpBatch.End();
            }

            int zStart = (int)(-layerDepth);
            int zLen = (int)(layerDepth * 2f);

            Color faceFront = new Color((byte)30, (byte)95, (byte)145, (byte)120);
            Color faceBack = new Color((byte)20, (byte)70, (byte)115, (byte)105);
            Color sideTint = new Color((byte)22, (byte)82, (byte)128, (byte)110);
            Color topTint = new Color((byte)70, (byte)160, (byte)220, (byte)92);

            foreach (var hull in Hull.HullList)
            {
                try
                {
                    if (hull == null || hull.Submarine == null) continue;
                    if (hull.WaterVolume <= 0f) continue;

                    var wr = hull.WorldRect;

                    float pct = MathHelper.Clamp(hull.WaterVolume / (hull.Rect.Width * hull.Rect.Height), 0f, 1f);
                    int waterH = Math.Max(1, (int)(wr.Height * pct));
                    if (waterH <= 0) continue;

                    // Верх воды в world-Y
                    int waterTopY = wr.Y - wr.Height + waterH;

                    // ---- Передняя грань воды (z = +depth) ----
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.NonPremultiplied,
                        SamplerState.PointClamp,
                        depthReadState,
                        RasterizerState.CullNone,
                        null,
                        BuildWallMatrix(+layerDepth - 0.5f));

                    fpBatch.Draw(
                        pixelTexture,
                        new Rectangle(wr.X, -waterTopY, wr.Width, waterH),
                        faceFront);

                    fpBatch.End();

                    // ---- Задняя грань воды (z = -depth) ----
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.NonPremultiplied,
                        SamplerState.PointClamp,
                        depthReadState,
                        RasterizerState.CullNone,
                        null,
                        BuildWallMatrix(-layerDepth + 0.5f));

                    fpBatch.Draw(
                        pixelTexture,
                        new Rectangle(wr.X, -waterTopY, wr.Width, waterH),
                        faceBack);

                    fpBatch.End();

                    // ---- Левый бок воды ----
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.NonPremultiplied,
                        SamplerState.PointClamp,
                        depthReadState,
                        RasterizerState.CullNone,
                        null,
                        BuildSideMatrix(wr.X));

                    fpBatch.Draw(
                        pixelTexture,
                        new Rectangle(zStart, -waterTopY, zLen, waterH),
                        sideTint);

                    fpBatch.End();

                    // ---- Правый бок воды ----
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.NonPremultiplied,
                        SamplerState.PointClamp,
                        depthReadState,
                        RasterizerState.CullNone,
                        null,
                        BuildSideMatrix(wr.X + wr.Width));

                    fpBatch.Draw(
                        pixelTexture,
                        new Rectangle(zStart, -waterTopY, zLen, waterH),
                        sideTint);

                    fpBatch.End();

                    // ---- Верхняя поверхность воды ----
                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.NonPremultiplied,
                        SamplerState.PointClamp,
                        depthReadState,
                        RasterizerState.CullNone,
                        null,
                        BuildFloorMatrix(waterTopY));

                    fpBatch.Draw(
                        pixelTexture,
                        new Rectangle(wr.X, zStart, wr.Width, zLen),
                        topTint);

                    fpBatch.End();
                }
                catch { }
            }
        }

        private static void DrawCharacters(Camera cam, Vector2 camPos)
        {
            var controlled = Character.Controlled;

            foreach (var c in Character.CharacterList)
            {
                try
                {
                    if (!c.IsVisible || c == controlled || Vector2.Distance(camPos, c.WorldPosition) > 10000f) continue;

                    if (characterRenderMode == 0)
                    {
                        // Режим 0: стандартный 2D
                        fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, null, RasterizerState.CullNone, null, BuildWallMatrix(0f));
                        c.Draw(fpBatch, cam);
                        fpBatch.End();
                    }
                    else
                    {
                        DrawCharacter3D(c, cam);
                    }
                }
                catch { }
            }
        }

        
       

        // ==================== ГЛАВНАЯ ФУНКЦИЯ ОТРИСОВКИ ПЕРСОНАЖА ====================
        
        
        
       
        
        
        // ==================== ЗАМЕНИ DrawCharacter3D ПОЛНОСТЬЮ ====================

        private static void DrawCharacter3D(Character c, Camera cam)
        {
            if (c.AnimController?.Limbs == null) return;

            float globalThicknessMultiplier = characterRenderMode switch
            {
                0 => 0f,
                1 => 1.0f,
                2 => 1.5f,
                3 => 2.0f,
                4 => 2.5f,
                5 => 3.0f,
                6 => 3.5f,
                7 => 4.0f,
                8 => 5.0f,
                9 => 6.0f,
                10 => 8.0f,
                _ => 2.5f
            };

            if (characterRenderMode == 0)
            {
                var ds = characterDepthMode == 0 ? DepthStencilState.Default : DepthStencilState.None;

                fpBatch.Begin(
                    SpriteSortMode.BackToFront,
                    BlendState.NonPremultiplied,
                    null,
                    ds,
                    RasterizerState.CullNone,
                    null,
                    BuildWallMatrix(0f));

                c.Draw(fpBatch, cam);
                fpBatch.End();
                return;
            }

            var limbsToRender = new List<Limb>();
            for (int i = 0; i < c.AnimController.Limbs.Length; i++)
            {
                var limb = c.AnimController.Limbs[i];
                if (limb == null) continue;
                limbsToRender.Add(limb);
            }

            // Сортировка для стабильности
            limbsToRender.Sort((a, b) =>
            {
                float depthA = a.ActiveSprite?.Depth ?? 0.5f;
                float depthB = b.ActiveSprite?.Depth ?? 0.5f;
                return depthB.CompareTo(depthA);
            });

            float sinYaw = MathF.Sin(camYaw);
            if (sinYaw <= 0) limbsToRender.Reverse();

            (float zOffset, float yOffset) GetOffsets(LimbType t)
            {
                return t switch
                {
                    LimbType.Head => (headOffset, headYOffset),
                    LimbType.Torso => (torsoOffset, torsoYOffset),
                    LimbType.Waist => (waistOffset, waistYOffset),

                    LimbType.LeftArm => (leftArmOffset, leftArmYOffset),
                    LimbType.LeftForearm => (leftForearmOffset, leftForearmYOffset),
                    LimbType.LeftHand => (leftHandOffset, leftHandYOffset),

                    LimbType.RightArm => (rightArmOffset, rightArmYOffset),
                    LimbType.RightForearm => (rightForearmOffset, rightForearmYOffset),
                    LimbType.RightHand => (rightHandOffset, rightHandYOffset),

                    LimbType.LeftThigh => (leftThighOffset, leftThighYOffset),
                    LimbType.LeftLeg => (leftLegOffset, leftLegYOffset),
                    LimbType.LeftFoot => (leftFootOffset, leftFootYOffset),

                    LimbType.RightThigh => (rightThighOffset, rightThighYOffset),
                    LimbType.RightLeg => (rightLegOffset, rightLegYOffset),
                    LimbType.RightFoot => (rightFootOffset, rightFootYOffset),

                    _ => (0f, 0f)
                };
            }

            float dirSign = (c.AnimController?.Dir ?? 1f) >= 0f ? 1f : -1f;

            foreach (var limb in limbsToRender)
            {
                var (_, thickness) = CalculateLimbParamsAuto(limb);
                thickness *= globalThicknessMultiplier;

                var (manualZRaw, manualY) = GetOffsets(limb.type);

                bool isLeftArmPart =
                    limb.type == LimbType.LeftArm ||
                    limb.type == LimbType.LeftForearm ||
                    limb.type == LimbType.LeftHand;

                bool isRightArmPart =
                    limb.type == LimbType.RightArm ||
                    limb.type == LimbType.RightForearm ||
                    limb.type == LimbType.RightHand;

                bool isLeftLegPart =
                    limb.type == LimbType.LeftThigh ||
                    limb.type == LimbType.LeftLeg ||
                    limb.type == LimbType.LeftFoot;

                bool isRightLegPart =
                    limb.type == LimbType.RightThigh ||
                    limb.type == LimbType.RightLeg ||
                    limb.type == LimbType.RightFoot;

                float manualZ;

                if (isLeftArmPart)
                {
                    // Вся левая цепочка руки на одном Z + зеркалится при развороте
                    float armZ = MathF.Abs(leftArmOffset);
                    if (armZ < 12f) armZ = 12f;
                    manualZ = -armZ * dirSign;
                }
                else if (isRightArmPart)
                {
                    // Вся правая цепочка руки на одном Z + зеркалится при развороте
                    float armZ = MathF.Abs(rightArmOffset);
                    if (armZ < 12f) armZ = 12f;
                    manualZ = +armZ * dirSign;
                }
                else if (isLeftLegPart || isRightLegPart)
                {
                    // Ноги оставляем по прежней схеме
                    float zAbs = MathF.Abs(manualZRaw);
                    if (zAbs < 12f) zAbs = 12f;
                    manualZ = isLeftLegPart ? -zAbs * dirSign : +zAbs * dirSign;
                }
                else
                {
                    // Голова/торс/талия
                    manualZ = manualZRaw;
                }

                DrawLimbExtrudedAuto(limb, manualZ, thickness, manualY, cam);
            }

            if (showLimbDebug)
            {
                foreach (var limb in limbsToRender)
                {
                    Vector2 pos = new Vector2(limb.body.DrawPosition.X, limb.body.DrawPosition.Y);

                    fpBatch.Begin(
                        SpriteSortMode.Deferred,
                        BlendState.NonPremultiplied,
                        null,
                        null,
                        RasterizerState.CullNone,
                        null,
                        BuildWallMatrix(0f));

                    fpBatch.Draw(pixelTexture, new Rectangle((int)pos.X - 5, -(int)pos.Y - 5, 10, 10), Color.Red);
                    fpBatch.End();
                }
            }
        }
		
		
		
		
		
        // Вспомогательная функция для получения спрайта
       
        private static Sprite GetLimbSprite(Limb limb)
        {
            // Пробуем все варианты получения спрайта
            if (limb.ActiveSprite?.Texture != null && !limb.ActiveSprite.Texture.IsDisposed) 
                return limb.ActiveSprite;
            
            if (limb.DeformSprite?.Sprite?.Texture != null && !limb.DeformSprite.Sprite.Texture.IsDisposed) 
                return limb.DeformSprite.Sprite;
            
            if (limb.Sprite?.Texture != null && !limb.Sprite.Texture.IsDisposed) 
                return limb.Sprite;
            
            return null;
        }

        private static void EnsureDepthStatesInitialized()
        {
            if (depthWriteState == null || depthWriteState.IsDisposed)
            {
                depthWriteState = new DepthStencilState
                {
                    DepthBufferEnable = true,
                    DepthBufferWriteEnable = true,
                    DepthBufferFunction = CompareFunction.LessEqual
                };
            }

            if (depthReadState == null || depthReadState.IsDisposed)
            {
                depthReadState = new DepthStencilState
                {
                    DepthBufferEnable = true,
                    DepthBufferWriteEnable = false, // читать глубину, но не писать
                    DepthBufferFunction = CompareFunction.LessEqual
                };
            }
        }


        // ==================== ПЛОСКАЯ ОТРИСОВКА (2D) ====================
        
        private static void DrawLimbFlat(Limb limb, float zOffset, Camera cam)
        {
            fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, depthWriteState,
                RasterizerState.CullNone, null, BuildWallMatrix(zOffset));
            
            try
            {
                var sprite = limb.ActiveSprite;
                if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;
                
                Vector2 pos = new Vector2(limb.body.DrawPosition.X, -limb.body.DrawPosition.Y);
                float rotation = -limb.body.DrawRotation;
                float scale = limb.Scale * limb.TextureScale;
                Color color = limb.character.Info?.Head.SkinColor ?? Color.White;
                
                SpriteEffects effect = limb.character.AnimController?.Dir > 0 
                    ? SpriteEffects.None 
                    : SpriteEffects.FlipHorizontally;
                
                sprite.Draw(fpBatch, pos, color, sprite.Origin, rotation, 
                    new Vector2(scale, scale), effect, sprite.Depth);
            }
            catch { }
            
            fpBatch.End();
        }
        
        // ==================== ДОБАВЬ НОВУЮ ФУНКЦИЮ ====================
        private static void DrawSingleSprite3D(Limb limb, Sprite sprite, float zCenter, float thickness, Camera cam)
        {
            if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;
            
            Texture2D tex = sprite.Texture;
            Rectangle srcRect = sprite.SourceRect;
            if (srcRect.Width <= 0 || srcRect.Height <= 0) return;
            
            var gd = GameMain.Instance.GraphicsDevice;
            
            Vector2 pos = new Vector2(limb.body.DrawPosition.X, limb.body.DrawPosition.Y);
            float rotation = limb.body.DrawRotation;
            float scale = limb.Scale * limb.TextureScale;
            
            float limbWidth = srcRect.Width * scale;
            float limbHeight = srcRect.Height * scale;
            float halfW = limbWidth / 2f;
            float halfH = limbHeight / 2f;
            
            if (thickness < 5f) thickness = 5f;
            
            float halfThickness = thickness / 2f;
            float zBack = zCenter - halfThickness;
            float zFront = zCenter + halfThickness;
            
            Color colorMain = Color.Magenta;
            Color colorSide = Color.Yellow;
            
            float cos = MathF.Cos(-rotation);
            float sin = MathF.Sin(-rotation);
            
            Vector2 RotatePoint(Vector2 p) => new Vector2(
                p.X * cos - p.Y * sin,
                p.X * sin + p.Y * cos
            );
            
            float texW = tex.Width;
            float texH = tex.Height;
            float u0 = srcRect.X / texW;
            float v0 = srcRect.Y / texH;
            float u1 = (srcRect.X + srcRect.Width) / texW;
            float v1 = (srcRect.Y + srcRect.Height) / texH;
            
            bool facingLeft = false;
            try { facingLeft = limb.character.AnimController?.Dir < 0; } catch { }
            
            if (facingLeft)
            {
                float temp = u0; u0 = u1; u1 = temp;
            }
            
            float pixelU = 2f / texW;
            float pixelV = 2f / texH;
            
            float uLeftEdge = u0;
            float uLeftEdge2 = u0 + pixelU;
            float uRightEdge = u1;
            float uRightEdge2 = u1 - pixelU;
            float vTopEdge = v0;
            float vTopEdge2 = v0 + pixelV;
            float vBottomEdge = v1;
            float vBottomEdge2 = v1 - pixelV;
            
            SetupBasicEffect3D();
            
            gd.DepthStencilState = characterDepthMode == 0 ? DepthStencilState.Default : DepthStencilState.None;
            gd.RasterizerState = RasterizerState.CullNone;
            gd.BlendState = BlendState.NonPremultiplied;
            gd.SamplerStates[0] = SamplerState.LinearClamp;
            
            basicEffect.Texture = tex;
            
            Vector2 tl = RotatePoint(new Vector2(-halfW, halfH));
            Vector2 tr = RotatePoint(new Vector2(halfW, halfH));
            Vector2 bl = RotatePoint(new Vector2(-halfW, -halfH));
            Vector2 br = RotatePoint(new Vector2(halfW, -halfH));
            
            // 1. ЗАДНЯЯ ГРАНЬ (zBack)
            quadVertices[0] = new VertexPositionColorTexture(new Vector3(pos.X + tl.X, pos.Y + tl.Y, zBack), colorMain, new Vector2(u0, v0));
            quadVertices[1] = new VertexPositionColorTexture(new Vector3(pos.X + tr.X, pos.Y + tr.Y, zBack), colorMain, new Vector2(u1, v0));
            quadVertices[2] = new VertexPositionColorTexture(new Vector3(pos.X + bl.X, pos.Y + bl.Y, zBack), colorMain, new Vector2(u0, v1));
            quadVertices[3] = new VertexPositionColorTexture(new Vector3(pos.X + br.X, pos.Y + br.Y, zBack), colorMain, new Vector2(u1, v1));
            
            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
            }
            
            // 2. ПЕРЕДНЯЯ ГРАНЬ (zFront)
            quadVertices[0] = new VertexPositionColorTexture(new Vector3(pos.X + tl.X, pos.Y + tl.Y, zFront), colorMain, new Vector2(u0, v0));
            quadVertices[1] = new VertexPositionColorTexture(new Vector3(pos.X + tr.X, pos.Y + tr.Y, zFront), colorMain, new Vector2(u1, v0));
            quadVertices[2] = new VertexPositionColorTexture(new Vector3(pos.X + bl.X, pos.Y + bl.Y, zFront), colorMain, new Vector2(u0, v1));
            quadVertices[3] = new VertexPositionColorTexture(new Vector3(pos.X + br.X, pos.Y + br.Y, zFront), colorMain, new Vector2(u1, v1));
            
            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
            }
            
            // 3. ВЕРХНЯЯ ГРАНЬ
            quadVertices[0] = new VertexPositionColorTexture(new Vector3(pos.X + tl.X, pos.Y + tl.Y, zBack), colorSide, new Vector2(u0, vTopEdge));
            quadVertices[1] = new VertexPositionColorTexture(new Vector3(pos.X + tr.X, pos.Y + tr.Y, zBack), colorSide, new Vector2(u1, vTopEdge));
            quadVertices[2] = new VertexPositionColorTexture(new Vector3(pos.X + tl.X, pos.Y + tl.Y, zFront), colorSide, new Vector2(u0, vTopEdge2));
            quadVertices[3] = new VertexPositionColorTexture(new Vector3(pos.X + tr.X, pos.Y + tr.Y, zFront), colorSide, new Vector2(u1, vTopEdge2));
            
            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
            }
            
            // 4. НИЖНЯЯ ГРАНЬ
            quadVertices[0] = new VertexPositionColorTexture(new Vector3(pos.X + bl.X, pos.Y + bl.Y, zBack), colorSide, new Vector2(u0, vBottomEdge2));
            quadVertices[1] = new VertexPositionColorTexture(new Vector3(pos.X + br.X, pos.Y + br.Y, zBack), colorSide, new Vector2(u1, vBottomEdge2));
            quadVertices[2] = new VertexPositionColorTexture(new Vector3(pos.X + bl.X, pos.Y + bl.Y, zFront), colorSide, new Vector2(u0, vBottomEdge));
            quadVertices[3] = new VertexPositionColorTexture(new Vector3(pos.X + br.X, pos.Y + br.Y, zFront), colorSide, new Vector2(u1, vBottomEdge));
            
            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
            }
            
            // 5. ПРАВАЯ ГРАНЬ
            quadVertices[0] = new VertexPositionColorTexture(new Vector3(pos.X + tr.X, pos.Y + tr.Y, zBack), colorSide, new Vector2(uRightEdge2, v0));
            quadVertices[1] = new VertexPositionColorTexture(new Vector3(pos.X + tr.X, pos.Y + tr.Y, zFront), colorSide, new Vector2(uRightEdge, v0));
            quadVertices[2] = new VertexPositionColorTexture(new Vector3(pos.X + br.X, pos.Y + br.Y, zBack), colorSide, new Vector2(uRightEdge2, v1));
            quadVertices[3] = new VertexPositionColorTexture(new Vector3(pos.X + br.X, pos.Y + br.Y, zFront), colorSide, new Vector2(uRightEdge, v1));
            
            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
            }
            
            // 6. ЛЕВАЯ ГРАНЬ
            quadVertices[0] = new VertexPositionColorTexture(new Vector3(pos.X + tl.X, pos.Y + tl.Y, zBack), colorSide, new Vector2(uLeftEdge, v0));
            quadVertices[1] = new VertexPositionColorTexture(new Vector3(pos.X + tl.X, pos.Y + tl.Y, zFront), colorSide, new Vector2(uLeftEdge2, v0));
            quadVertices[2] = new VertexPositionColorTexture(new Vector3(pos.X + bl.X, pos.Y + bl.Y, zBack), colorSide, new Vector2(uLeftEdge, v1));
            quadVertices[3] = new VertexPositionColorTexture(new Vector3(pos.X + bl.X, pos.Y + bl.Y, zFront), colorSide, new Vector2(uLeftEdge2, v1));
            
            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
            }
            
            gd.DepthStencilState = depthWriteState;
        }





        
        // ==================== ЗАМЕНИ DrawLimbExtrudedAuto ПОЛНОСТЬЮ ====================
        private static void DrawLimbExtrudedAuto(Limb limb, float zCenter, float thickness, float yOffset, Camera cam)
        {
            if (limb == null) return;

            Sprite sprite = GetLimbSprite(limb);
            if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;

            Rectangle srcRect = sprite.SourceRect;
            if (srcRect.Width <= 0 || srcRect.Height <= 0) return;

            // Мягкий порог для проблемной правой руки, остальным — твой жесткий
            bool isRightArmGroup =
                limb.type == LimbType.RightArm ||
                limb.type == LimbType.RightForearm ||
                limb.type == LimbType.RightHand;

            byte alphaThreshold = isRightArmGroup ? (byte)80 : (byte)160;

            var mesh = GetOrBuildExtrudedMesh(sprite.Texture, srcRect, alphaThreshold);

            float finalThickness = Math.Max(thickness, 16f);
            DrawExtrudedMesh(limb, mesh, zCenter, finalThickness, yOffset);
        }
		
		
		
        // ==================== ДОБАВЬ ЭТУ ФУНКЦИЮ ====================
        private static Color GetAverageEdgeColor(Texture2D tex, Rectangle srcRect)
        {
            // Кэшируем результат
            string key = $"{tex.GetHashCode()}_{srcRect.X}_{srcRect.Y}_{srcRect.Width}_{srcRect.Height}";
            
            try
            {
                Color[] pixels = new Color[srcRect.Width * srcRect.Height];
                tex.GetData(0, srcRect, pixels, 0, pixels.Length);
                
                int r = 0, g = 0, b = 0, count = 0;
                int w = srcRect.Width;
                int h = srcRect.Height;
                
                // Сэмплируем пиксели по периметру
                for (int x = 0; x < w; x += 4)
                {
                    // Верхний край
                    Color c = pixels[x];
                    if (c.A > 50) { r += c.R; g += c.G; b += c.B; count++; }
                    
                    // Нижний край
                    c = pixels[(h - 1) * w + x];
                    if (c.A > 50) { r += c.R; g += c.G; b += c.B; count++; }
                }
                
                for (int y = 0; y < h; y += 4)
                {
                    // Левый край
                    Color c = pixels[y * w];
                    if (c.A > 50) { r += c.R; g += c.G; b += c.B; count++; }
                    
                    // Правый край
                    c = pixels[y * w + w - 1];
                    if (c.A > 50) { r += c.R; g += c.G; b += c.B; count++; }
                }
                
                if (count > 0)
                {
                    return new Color(r / count, g / count, b / count);
                }
            }
            catch { }
            
            return new Color(128, 100, 80); // Дефолтный цвет если не удалось
        }




        private static void DrawLimbSimple3D(Limb limb, float zCenter, float thickness, float yOffset, Camera cam)
        {
            var sprite = limb.ActiveSprite;
            if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;

            if (thickness <= 0)
            {
                fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, depthWriteState,
                    RasterizerState.CullNone, null, BuildWallMatrix(zCenter));
                DrawLimb(fpBatch, limb, cam, yOffset);
                fpBatch.End();
                return;
            }

            float zBack = zCenter - thickness;
            float zFront = zCenter + thickness;

            float sinYaw = MathF.Sin(camYaw);

            // Рисуем в правильном порядке в зависимости от направления камеры
            if (sinYaw > 0)
            {
                // Камера смотрит в +Z, сначала задний слой
                fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, depthWriteState,
                    RasterizerState.CullNone, null, BuildWallMatrix(zBack));
                DrawLimbTinted(fpBatch, limb, cam, 0.6f, yOffset);
                fpBatch.End();

                fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, depthWriteState,
                    RasterizerState.CullNone, null, BuildWallMatrix(zFront));
                DrawLimbTinted(fpBatch, limb, cam, 1.1f, yOffset);
                fpBatch.End();
            }
            else
            {
                // Камера смотрит в -Z, сначала передний слой
                fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, depthWriteState,
                    RasterizerState.CullNone, null, BuildWallMatrix(zFront));
                DrawLimbTinted(fpBatch, limb, cam, 1.1f, yOffset);
                fpBatch.End();

                fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, depthWriteState,
                    RasterizerState.CullNone, null, BuildWallMatrix(zBack));
                DrawLimbTinted(fpBatch, limb, cam, 0.6f, yOffset);
                fpBatch.End();
            }
        }
        
       
        
        
        private static void DrawLimbExtruded(Limb limb, float zCenter, float thickness, float yOffset, Camera cam)
        {
            var sprite = limb.ActiveSprite;
            if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;
        
            if (thickness <= 0)
            {
                fpBatch.Begin(SpriteSortMode.BackToFront, BlendState.NonPremultiplied, null, depthWriteState,
                    RasterizerState.CullNone, null, BuildWallMatrix(zCenter));
                DrawLimb(fpBatch, limb, cam, yOffset);
                fpBatch.End();
                return;
            }
        
            var gd = GameMain.Instance.GraphicsDevice;
        
            var (frontOff, backOff, topOff, bottomOff, leftOff, rightOff) = GetFaceOffsets(limb.type);
            var scales = GetFaceScales(limb.type);
            float fScX = scales.fScaleX, fScY = scales.fScaleY;
            float bScX = scales.bScaleX, bScY = scales.bScaleY;
            float tScX = scales.tScaleX, tScY = scales.tScaleY;
            float boScX = scales.boScaleX, boScY = scales.boScaleY;
            float lScX = scales.lScaleX, lScY = scales.lScaleY;
            float rScX = scales.rScaleX, rScY = scales.rScaleY;
        
            Vector2 pos = new Vector2(limb.body.DrawPosition.X, limb.body.DrawPosition.Y + yOffset);
            float rotation = limb.body.DrawRotation;
            float scale = limb.Scale * limb.TextureScale;
        
            float limbWidth = sprite.SourceRect.Width * scale;
            float limbHeight = sprite.SourceRect.Height * scale;
        
            float halfW = limbWidth / 2f;
            float halfH = limbHeight / 2f;
        
            float zBack = zCenter - thickness;
            float zFront = zCenter + thickness;
        
            Color baseColor = limb.character.Info?.Head.SkinColor ?? Color.White;
            Color colorFront = Mult(baseColor, 1.1f);
            Color colorBack = Mult(baseColor, 0.7f);
            Color colorSide = Mult(baseColor, 0.85f);
            Color colorTop = Mult(baseColor, 0.9f);
            Color colorBottom = Mult(baseColor, 0.6f);
        
            Color bgColor = limbBackgroundMode switch
            {
                1 => Color.White,
                2 => Color.Black,
                3 => baseColor,
                _ => Color.Transparent
            };
        
            float cos = MathF.Cos(-rotation);
            float sin = MathF.Sin(-rotation);
        
            Vector2 RotatePoint(Vector2 p)
            {
                return new Vector2(
                    p.X * cos - p.Y * sin,
                    p.X * sin + p.Y * cos
                );
            }
        
            Texture2D tex = sprite.Texture;
            Rectangle srcRect = sprite.SourceRect;
            float texW = tex.Width;
            float texH = tex.Height;
            float u0 = srcRect.X / texW;
            float v0 = srcRect.Y / texH;
            float u1 = (srcRect.X + srcRect.Width) / texW;
            float v1 = (srcRect.Y + srcRect.Height) / texH;
        
            // ========== ПРОСТОЙ СПОСОБ: отступ от краёв ==========
            float padding = texturePadding;
            float bgLeft = -halfW * (1f - padding);
            float bgRight = halfW * (1f - padding);
            float bgTop = halfH * (1f - padding);
            float bgBottom = -halfH * (1f - padding);
        
            SetupBasicEffect3D();
        
            if (characterDepthMode == 0)
                gd.DepthStencilState = DepthStencilState.Default;
            else
                gd.DepthStencilState = characterDepthMode == 0 ? DepthStencilState.Default : DepthStencilState.None;
            
            gd.RasterizerState = RasterizerState.CullNone;
            gd.BlendState = BlendState.NonPremultiplied;
            gd.SamplerStates[0] = SamplerState.LinearClamp;
        
            // ========== ПЕРЕДНЯЯ ГРАНЬ ==========
            {
                float sW = halfW * fScX;
                float sH = halfH * fScY;
                float z = zFront + frontOff;
        
                Vector2 tl = RotatePoint(new Vector2(-sW, sH));
                Vector2 tr = RotatePoint(new Vector2(sW, sH));
                Vector2 bl = RotatePoint(new Vector2(-sW, -sH));
                Vector2 br = RotatePoint(new Vector2(sW, -sH));
        
                Vector3 v0p = new Vector3(pos.X + tl.X, pos.Y + tl.Y, z);
                Vector3 v1p = new Vector3(pos.X + tr.X, pos.Y + tr.Y, z);
                Vector3 v2p = new Vector3(pos.X + bl.X, pos.Y + bl.Y, z);
                Vector3 v3p = new Vector3(pos.X + br.X, pos.Y + br.Y, z);
        
                // Фон позади текстуры - с отступом
                if (limbBackgroundMode > 0)
                {
                    float scaledBgLeft = bgLeft * fScX;
                    float scaledBgRight = bgRight * fScX;
                    float scaledBgTop = bgTop * fScY;
                    float scaledBgBottom = bgBottom * fScY;
        
                    Vector2 bgTL = RotatePoint(new Vector2(scaledBgLeft, scaledBgTop));
                    Vector2 bgTR = RotatePoint(new Vector2(scaledBgRight, scaledBgTop));
                    Vector2 bgBL = RotatePoint(new Vector2(scaledBgLeft, scaledBgBottom));
                    Vector2 bgBR = RotatePoint(new Vector2(scaledBgRight, scaledBgBottom));
        
                    basicEffect.Texture = pixelTexture;
                    Color bgTint = Mult(bgColor, 0.95f);
                    quadVertices[0] = new VertexPositionColorTexture(new Vector3(pos.X + bgTL.X, pos.Y + bgTL.Y, z - 0.1f), bgTint, Vector2.Zero);
                    quadVertices[1] = new VertexPositionColorTexture(new Vector3(pos.X + bgTR.X, pos.Y + bgTR.Y, z - 0.1f), bgTint, Vector2.Zero);
                    quadVertices[2] = new VertexPositionColorTexture(new Vector3(pos.X + bgBL.X, pos.Y + bgBL.Y, z - 0.1f), bgTint, Vector2.Zero);
                    quadVertices[3] = new VertexPositionColorTexture(new Vector3(pos.X + bgBR.X, pos.Y + bgBR.Y, z - 0.1f), bgTint, Vector2.Zero);
                    foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                    }
                }
        
                // Текстура
                basicEffect.Texture = tex;
                quadVertices[0] = new VertexPositionColorTexture(v0p, colorFront, new Vector2(u0, v0));
                quadVertices[1] = new VertexPositionColorTexture(v1p, colorFront, new Vector2(u1, v0));
                quadVertices[2] = new VertexPositionColorTexture(v2p, colorFront, new Vector2(u0, v1));
                quadVertices[3] = new VertexPositionColorTexture(v3p, colorFront, new Vector2(u1, v1));
                foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                }
            }
        
            // ========== ЗАДНЯЯ ГРАНЬ ==========
            {
                float sW = halfW * bScX;
                float sH = halfH * bScY;
                float z = zBack + backOff;
        
                Vector2 tl = RotatePoint(new Vector2(-sW, sH));
                Vector2 tr = RotatePoint(new Vector2(sW, sH));
                Vector2 bl = RotatePoint(new Vector2(-sW, -sH));
                Vector2 br = RotatePoint(new Vector2(sW, -sH));
        
                Vector3 v0p = new Vector3(pos.X + tl.X, pos.Y + tl.Y, z);
                Vector3 v1p = new Vector3(pos.X + tr.X, pos.Y + tr.Y, z);
                Vector3 v2p = new Vector3(pos.X + bl.X, pos.Y + bl.Y, z);
                Vector3 v3p = new Vector3(pos.X + br.X, pos.Y + br.Y, z);
        
                // Фон - с отступом
                if (limbBackgroundMode > 0)
                {
                    float scaledBgLeft = bgLeft * bScX;
                    float scaledBgRight = bgRight * bScX;
                    float scaledBgTop = bgTop * bScY;
                    float scaledBgBottom = bgBottom * bScY;
        
                    Vector2 bgTL = RotatePoint(new Vector2(scaledBgLeft, scaledBgTop));
                    Vector2 bgTR = RotatePoint(new Vector2(scaledBgRight, scaledBgTop));
                    Vector2 bgBL = RotatePoint(new Vector2(scaledBgLeft, scaledBgBottom));
                    Vector2 bgBR = RotatePoint(new Vector2(scaledBgRight, scaledBgBottom));
        
                    basicEffect.Texture = pixelTexture;
                    Color bgTint = Mult(bgColor, 0.7f);
                    quadVertices[0] = new VertexPositionColorTexture(new Vector3(pos.X + bgTL.X, pos.Y + bgTL.Y, z + 0.1f), bgTint, Vector2.Zero);
                    quadVertices[1] = new VertexPositionColorTexture(new Vector3(pos.X + bgTR.X, pos.Y + bgTR.Y, z + 0.1f), bgTint, Vector2.Zero);
                    quadVertices[2] = new VertexPositionColorTexture(new Vector3(pos.X + bgBL.X, pos.Y + bgBL.Y, z + 0.1f), bgTint, Vector2.Zero);
                    quadVertices[3] = new VertexPositionColorTexture(new Vector3(pos.X + bgBR.X, pos.Y + bgBR.Y, z + 0.1f), bgTint, Vector2.Zero);
                    foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                    }
                }
        
                // Текстура
                basicEffect.Texture = tex;
                quadVertices[0] = new VertexPositionColorTexture(v0p, colorBack, new Vector2(u0, v0));
                quadVertices[1] = new VertexPositionColorTexture(v1p, colorBack, new Vector2(u1, v0));
                quadVertices[2] = new VertexPositionColorTexture(v2p, colorBack, new Vector2(u0, v1));
                quadVertices[3] = new VertexPositionColorTexture(v3p, colorBack, new Vector2(u1, v1));
                foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                }
            }
        
            // ========== ВЕРХНЯЯ ГРАНЬ ==========
            {
                float sW = halfW * tScX;
                float depth = thickness * 2f * tScY;
                
                // Используем bgLeft/bgRight для ширины верхней грани
                float bgSW = halfW * (1f - padding) * tScX;
                
                Vector2 fl = RotatePoint(new Vector2(-bgSW, 0));
                Vector2 fr = RotatePoint(new Vector2(bgSW, 0));
                
                float yTop = halfH * (1f - padding) + topOff;
                Vector2 topOffset = RotatePoint(new Vector2(0, yTop));
                
                float halfDepth = depth / 2f;
                
                Vector3 backL = new Vector3(pos.X + fl.X + topOffset.X, pos.Y + topOffset.Y, zCenter - halfDepth);
                Vector3 backR = new Vector3(pos.X + fr.X + topOffset.X, pos.Y + topOffset.Y, zCenter - halfDepth);
                Vector3 frontL = new Vector3(pos.X + fl.X + topOffset.X, pos.Y + topOffset.Y, zCenter + halfDepth);
                Vector3 frontR = new Vector3(pos.X + fr.X + topOffset.X, pos.Y + topOffset.Y, zCenter + halfDepth);
        
                Color faceColor = limbBackgroundMode > 0 ? Mult(bgColor, 0.9f) : colorTop;
                basicEffect.Texture = pixelTexture;
                quadVertices[0] = new VertexPositionColorTexture(backL, faceColor, Vector2.Zero);
                quadVertices[1] = new VertexPositionColorTexture(backR, faceColor, Vector2.Zero);
                quadVertices[2] = new VertexPositionColorTexture(frontL, faceColor, Vector2.Zero);
                quadVertices[3] = new VertexPositionColorTexture(frontR, faceColor, Vector2.Zero);
                foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                }
            }
        
            // ========== НИЖНЯЯ ГРАНЬ ==========
            {
                float sW = halfW * boScX;
                float depth = thickness * 2f * boScY;
                
                float bgSW = halfW * (1f - padding) * boScX;
                
                Vector2 fl = RotatePoint(new Vector2(-bgSW, 0));
                Vector2 fr = RotatePoint(new Vector2(bgSW, 0));
                
                float yBottom = -halfH * (1f - padding) + bottomOff;
                Vector2 bottomOffset = RotatePoint(new Vector2(0, yBottom));
                
                float halfDepth = depth / 2f;
                
                Vector3 frontL = new Vector3(pos.X + fl.X + bottomOffset.X, pos.Y + bottomOffset.Y, zCenter + halfDepth);
                Vector3 frontR = new Vector3(pos.X + fr.X + bottomOffset.X, pos.Y + bottomOffset.Y, zCenter + halfDepth);
                Vector3 backL = new Vector3(pos.X + fl.X + bottomOffset.X, pos.Y + bottomOffset.Y, zCenter - halfDepth);
                Vector3 backR = new Vector3(pos.X + fr.X + bottomOffset.X, pos.Y + bottomOffset.Y, zCenter - halfDepth);
        
                Color faceColor = limbBackgroundMode > 0 ? Mult(bgColor, 0.6f) : colorBottom;
                basicEffect.Texture = pixelTexture;
                quadVertices[0] = new VertexPositionColorTexture(frontL, faceColor, Vector2.Zero);
                quadVertices[1] = new VertexPositionColorTexture(frontR, faceColor, Vector2.Zero);
                quadVertices[2] = new VertexPositionColorTexture(backL, faceColor, Vector2.Zero);
                quadVertices[3] = new VertexPositionColorTexture(backR, faceColor, Vector2.Zero);
                foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                }
            }
        
            // ========== ЛЕВАЯ ГРАНЬ ==========
            {
                float sH = halfH * lScY;
                float depth = thickness * 2f * lScX;
                
                float bgSH = halfH * (1f - padding) * lScY;
                
                Vector2 tb = RotatePoint(new Vector2(0, bgSH));
                Vector2 bb = RotatePoint(new Vector2(0, -bgSH));
                
                float xLeft = -halfW * (1f - padding) + leftOff;
                Vector2 leftOffset = RotatePoint(new Vector2(xLeft, 0));
                
                float halfDepth = depth / 2f;
                
                Vector3 backT = new Vector3(pos.X + leftOffset.X + tb.X, pos.Y + leftOffset.Y + tb.Y, zCenter - halfDepth);
                Vector3 frontT = new Vector3(pos.X + leftOffset.X + tb.X, pos.Y + leftOffset.Y + tb.Y, zCenter + halfDepth);
                Vector3 backB = new Vector3(pos.X + leftOffset.X + bb.X, pos.Y + leftOffset.Y + bb.Y, zCenter - halfDepth);
                Vector3 frontB = new Vector3(pos.X + leftOffset.X + bb.X, pos.Y + leftOffset.Y + bb.Y, zCenter + halfDepth);
        
                Color faceColor = limbBackgroundMode > 0 ? Mult(bgColor, 0.8f) : colorSide;
                basicEffect.Texture = pixelTexture;
                quadVertices[0] = new VertexPositionColorTexture(backT, faceColor, Vector2.Zero);
                quadVertices[1] = new VertexPositionColorTexture(frontT, faceColor, Vector2.Zero);
                quadVertices[2] = new VertexPositionColorTexture(backB, faceColor, Vector2.Zero);
                quadVertices[3] = new VertexPositionColorTexture(frontB, faceColor, Vector2.Zero);
                foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                }
            }
        
            // ========== ПРАВАЯ ГРАНЬ ==========
            {
                float sH = halfH * rScY;
                float depth = thickness * 2f * rScX;
                
                float bgSH = halfH * (1f - padding) * rScY;
                
                Vector2 tb = RotatePoint(new Vector2(0, bgSH));
                Vector2 bb = RotatePoint(new Vector2(0, -bgSH));
                
                float xRight = halfW * (1f - padding) + rightOff;
                Vector2 rightOffset = RotatePoint(new Vector2(xRight, 0));
                
                float halfDepth = depth / 2f;
                
                Vector3 frontT = new Vector3(pos.X + rightOffset.X + tb.X, pos.Y + rightOffset.Y + tb.Y, zCenter + halfDepth);
                Vector3 backT = new Vector3(pos.X + rightOffset.X + tb.X, pos.Y + rightOffset.Y + tb.Y, zCenter - halfDepth);
                Vector3 frontB = new Vector3(pos.X + rightOffset.X + bb.X, pos.Y + rightOffset.Y + bb.Y, zCenter + halfDepth);
                Vector3 backB = new Vector3(pos.X + rightOffset.X + bb.X, pos.Y + rightOffset.Y + bb.Y, zCenter - halfDepth);
        
                Color faceColor = limbBackgroundMode > 0 ? Mult(bgColor, 0.8f) : colorSide;
                basicEffect.Texture = pixelTexture;
                quadVertices[0] = new VertexPositionColorTexture(frontT, faceColor, Vector2.Zero);
                quadVertices[1] = new VertexPositionColorTexture(backT, faceColor, Vector2.Zero);
                quadVertices[2] = new VertexPositionColorTexture(frontB, faceColor, Vector2.Zero);
                quadVertices[3] = new VertexPositionColorTexture(backB, faceColor, Vector2.Zero);
                foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                }
            }
        
            gd.DepthStencilState = depthWriteState;
        }

        private static void DrawTexturedQuad(GraphicsDevice gd,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br,
            float u0, float v0, float u1, float v1, Color color)
        {
            quadVertices[0] = new VertexPositionColorTexture(tl, color, new Vector2(u0, v0));
            quadVertices[1] = new VertexPositionColorTexture(tr, color, new Vector2(u1, v0));
            quadVertices[2] = new VertexPositionColorTexture(bl, color, new Vector2(u0, v1));
            quadVertices[3] = new VertexPositionColorTexture(br, color, new Vector2(u1, v1));

            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleList,
                    quadVertices, 0, 4,
                    quadIndices, 0, 2);
            }
        }

        private static void DrawSideFacesHorizontal(GraphicsDevice gd,
            Vector3 frontTL, Vector3 frontTR, Vector3 frontBL, Vector3 frontBR,
            Vector3 backTL, Vector3 backTR, Vector3 backBL, Vector3 backBR,
            Color colorSide, Color colorTop, Color colorBottom, float sinYaw)
        {
            basicEffect.Texture = pixelTexture;

            // Верхняя грань
            DrawColoredQuad(gd, backTL, backTR, frontTL, frontTR, colorTop);

            // Нижняя грань
            DrawColoredQuad(gd, frontBL, frontBR, backBL, backBR, colorBottom);

            // Передняя и задняя грани (по Z) теперь боковые
            if (sinYaw > 0)
            {
                DrawColoredQuad(gd, backTL, backTR, backBL, backBR, Mult(colorSide, 0.9f));
                DrawColoredQuad(gd, frontTR, frontTL, frontBR, frontBL, colorSide);
            }
            else
            {
                DrawColoredQuad(gd, frontTR, frontTL, frontBR, frontBL, colorSide);
                DrawColoredQuad(gd, backTL, backTR, backBL, backBR, Mult(colorSide, 0.9f));
            }
        }



      
        private static void DrawLimbCaps3D(Limb limb, Sprite sprite, float zCenter, float halfDepth, Color frontColor, Color backColor)
        {
            var gd = GameMain.Instance.GraphicsDevice;
            Texture2D tex = sprite.Texture;
            Rectangle srcRect = sprite.SourceRect;

            if (tex == null || tex.IsDisposed || srcRect.Width <= 0 || srcRect.Height <= 0) return;

            float scale = limb.Scale * limb.TextureScale;
            if (scale < 0.01f) scale = 1f;

            float rotation = -limb.body.DrawRotation;
            Vector2 pos = new Vector2(limb.body.DrawPosition.X, limb.body.DrawPosition.Y);

            bool facingLeft = false;
            try { facingLeft = limb.character?.AnimController?.Dir < 0; } catch { }

            bool isArmPart =
                limb.type == LimbType.LeftArm ||
                limb.type == LimbType.LeftForearm ||
                limb.type == LimbType.LeftHand ||
                limb.type == LimbType.RightArm ||
                limb.type == LimbType.RightForearm ||
                limb.type == LimbType.RightHand;

            // Для forearm/hand пробуем определять направление по родителю
            bool effectiveFacingLeft = facingLeft;
            try
            {
                if (limb.character?.AnimController?.Limbs != null)
                {
                    Limb parent = null;

                    if (limb.type == LimbType.LeftHand)
                    {
                        parent = limb.character.AnimController.Limbs.FirstOrDefault(l => l?.type == LimbType.LeftForearm);
                    }
                    else if (limb.type == LimbType.RightHand)
                    {
                        parent = limb.character.AnimController.Limbs.FirstOrDefault(l => l?.type == LimbType.RightForearm);
                    }
                    else if (limb.type == LimbType.LeftForearm)
                    {
                        parent = limb.character.AnimController.Limbs.FirstOrDefault(l => l?.type == LimbType.LeftArm);
                    }
                    else if (limb.type == LimbType.RightForearm)
                    {
                        parent = limb.character.AnimController.Limbs.FirstOrDefault(l => l?.type == LimbType.RightArm);
                    }

                    if (parent?.body != null)
                    {
                        float dx = limb.body.DrawPosition.X - parent.body.DrawPosition.X;
                        if (MathF.Abs(dx) > 0.1f) effectiveFacingLeft = dx < 0f;
                    }
                }
            }
            catch { }

            // Инвертируем только arm-chain
            bool renderFacingLeft = isArmPart ? !effectiveFacingLeft : effectiveFacingLeft;

            // Локальные координаты спрайта в origin-space
            float x0 = -sprite.Origin.X;
            float x1 = srcRect.Width - sprite.Origin.X;
            float y0 = sprite.Origin.Y;
            float y1 = sprite.Origin.Y - srcRect.Height;

            float texW = tex.Width;
            float texH = tex.Height;

            float uInset = 0.5f / texW;
            float vInset = 0.5f / texH;

            float u0 = srcRect.X / texW + uInset;
            float v0 = srcRect.Y / texH + vInset;
            float u1 = (srcRect.X + srcRect.Width) / texW - uInset;
            float v1 = (srcRect.Y + srcRect.Height) / texH - vInset;

            // Front UV
            float fu0 = u0, fu1 = u1;
            if (renderFacingLeft)
            {
                float t = fu0; fu0 = fu1; fu1 = t;
            }

            // Back UV (наружу)
            float bu0 = fu1;
            float bu1 = fu0;

            Matrix world =
                Matrix.CreateScale(scale, scale, 1f) *
                Matrix.CreateRotationZ(rotation) *
                Matrix.CreateTranslation(pos.X, pos.Y, zCenter);

            basicEffect.World = world;
            basicEffect.Texture = tex;

            Vector3 frontTL = new Vector3(x0, y0, +halfDepth);
            Vector3 frontTR = new Vector3(x1, y0, +halfDepth);
            Vector3 frontBL = new Vector3(x0, y1, +halfDepth);
            Vector3 frontBR = new Vector3(x1, y1, +halfDepth);

            Vector3 backTL = new Vector3(x0, y0, -halfDepth);
            Vector3 backTR = new Vector3(x1, y0, -halfDepth);
            Vector3 backBL = new Vector3(x0, y1, -halfDepth);
            Vector3 backBR = new Vector3(x1, y1, -halfDepth);

            float sinYaw = MathF.Sin(camYaw);
            if (sinYaw > 0f)
            {
                DrawTexturedFace(gd, tex, backTR, backTL, backBR, backBL, bu0, v0, bu1, v1, backColor);
                DrawTexturedFace(gd, tex, frontTL, frontTR, frontBL, frontBR, fu0, v0, fu1, v1, frontColor);
            }
            else
            {
                DrawTexturedFace(gd, tex, frontTL, frontTR, frontBL, frontBR, fu0, v0, fu1, v1, frontColor);
                DrawTexturedFace(gd, tex, backTR, backTL, backBR, backBL, bu0, v0, bu1, v1, backColor);
            }
        }




        private static void DrawTexturedFace(GraphicsDevice gd, Texture2D tex,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br,
            float u0, float v0, float u1, float v1, Color color)
        {
            basicEffect.Texture = tex;

            quadVertices[0] = new VertexPositionColorTexture(tl, color, new Vector2(u0, v0));
            quadVertices[1] = new VertexPositionColorTexture(tr, color, new Vector2(u1, v0));
            quadVertices[2] = new VertexPositionColorTexture(bl, color, new Vector2(u0, v1));
            quadVertices[3] = new VertexPositionColorTexture(br, color, new Vector2(u1, v1));

            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleList,
                    quadVertices, 0, 4,
                    quadIndices, 0, 2);
            }
        }

        private static void DrawSideFaces(GraphicsDevice gd,
            Vector3 frontTL, Vector3 frontTR, Vector3 frontBL, Vector3 frontBR,
            Vector3 backTL, Vector3 backTR, Vector3 backBL, Vector3 backBR,
            Color colorSide, Color colorTop, Color colorBottom, float cosYaw)
        {
            basicEffect.Texture = pixelTexture;

            // Верхняя грань
            DrawColoredQuad(gd, backTL, backTR, frontTL, frontTR, colorTop);

            // Нижняя грань
            DrawColoredQuad(gd, frontBL, frontBR, backBL, backBR, colorBottom);

            // Боковые грани в зависимости от направления камеры
            if (cosYaw > 0)
            {
                // Сначала левая (дальняя), потом правая (ближняя)
                DrawColoredQuad(gd, backTL, frontTL, backBL, frontBL, Mult(colorSide, 0.9f));
                DrawColoredQuad(gd, frontTR, backTR, frontBR, backBR, colorSide);
            }
            else
            {
                // Сначала правая (дальняя), потом левая (ближняя)
                DrawColoredQuad(gd, frontTR, backTR, frontBR, backBR, colorSide);
                DrawColoredQuad(gd, backTL, frontTL, backBL, frontBL, Mult(colorSide, 0.9f));
            }
        }

        private static void DrawColoredQuad(GraphicsDevice gd,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, Color color)
        {
            quadVertices[0] = new VertexPositionColorTexture(tl, color, Vector2.Zero);
            quadVertices[1] = new VertexPositionColorTexture(tr, color, Vector2.Zero);
            quadVertices[2] = new VertexPositionColorTexture(bl, color, Vector2.Zero);
            quadVertices[3] = new VertexPositionColorTexture(br, color, Vector2.Zero);

            foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
            {
                pass.Apply();
                gd.DrawUserIndexedPrimitives(
                    PrimitiveType.TriangleList,
                    quadVertices, 0, 4,
                    quadIndices, 0, 2);
            }
        }




        private static bool AddTexturedQuad(
            List<VertexPositionColorTexture> vertices,
            List<short> indices,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br,
            float u0, float v0, float u1, float v1,
            Color color)
        {
            if (vertices.Count > short.MaxValue - 4) return false;

            short baseIndex = (short)vertices.Count;
            vertices.Add(new VertexPositionColorTexture(tl, color, new Vector2(u0, v0)));
            vertices.Add(new VertexPositionColorTexture(tr, color, new Vector2(u1, v0)));
            vertices.Add(new VertexPositionColorTexture(bl, color, new Vector2(u0, v1)));
            vertices.Add(new VertexPositionColorTexture(br, color, new Vector2(u1, v1)));

            indices.Add(baseIndex);
            indices.Add((short)(baseIndex + 1));
            indices.Add((short)(baseIndex + 2));

            indices.Add((short)(baseIndex + 2));
            indices.Add((short)(baseIndex + 1));
            indices.Add((short)(baseIndex + 3));
            return true;
        }

        private static bool AddSolidQuad(
            List<VertexPositionColorTexture> vertices,
            List<short> indices,
            Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br,
            Color color)
        {
            return AddTexturedQuad(vertices, indices, tl, tr, bl, br, 0f, 0f, 1f, 1f, color);
        }

        private static ExtrudedSpriteMesh BuildExtrudedMeshFromAlpha(Texture2D tex, Rectangle srcRect, byte alphaThreshold = 16)
        {
            if (tex == null || tex.IsDisposed || srcRect.Width <= 0 || srcRect.Height <= 0) return null;

            try
            {
                int w = srcRect.Width;
                int h = srcRect.Height;

                // ВАЖНО: читаем через RT-копию (обход проблемы tex.GetData в Barotrauma)
                if (!TryGetSpriteRegionPixels(tex, srcRect, out var pixels) || pixels == null)
                {
                    return null;
                }

                bool[,] opaque = new bool[w, h];
                int opaqueCount = 0;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        bool isOpaque = pixels[y * w + x].A >= alphaThreshold;
                        opaque[x, y] = isOpaque;
                        if (isOpaque) opaqueCount++;
                    }
                }

                if (opaqueCount == 0) return null;

                var sideVertices = new List<VertexPositionColorTexture>(opaqueCount * 12);
                var sideIndices = new List<short>(opaqueCount * 18);

                float halfW = w * 0.5f;
                float halfH = h * 0.5f;

                bool IsOpaqueAt(int px, int py)
                {
                    if (px < 0 || px >= w || py < 0 || py >= h) return false;
                    return opaque[px, py];
                }

                const float zFront = 1f;
                const float zBack = -1f;

                float texW = tex.Width;
                float texH = tex.Height;

                // UV шаг 1 пиксель в текстуре
                float du = 1f / texW;
                float dv = 1f / texH;

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (!opaque[x, y]) continue;

                        float x0 = x - halfW;
                        float x1 = x0 + 1f;

                        float yTop = halfH - y;
                        float yBottom = yTop - 1f;

                        float u0 = (srcRect.X + x) / texW;
                        float u1 = (srcRect.X + x + 1f) / texW;
                        float v0 = (srcRect.Y + y) / texH;
                        float v1 = (srcRect.Y + y + 1f) / texH;


                        float uInL = MathHelper.Clamp(u0 + du * 0.75f, 0f, 1f);
                        float uInR = MathHelper.Clamp(u1 - du * 0.75f, 0f, 1f);
                        float vInT = MathHelper.Clamp(v0 + dv * 0.75f, 0f, 1f);
                        float vInB = MathHelper.Clamp(v1 - dv * 0.75f, 0f, 1f);
                        
                        float uSpan0 = MathHelper.Clamp(u0 + du * 0.20f, 0f, 1f);
                        float uSpan1 = MathHelper.Clamp(u1 - du * 0.20f, 0f, 1f);
                        float vSpan0 = MathHelper.Clamp(v0 + dv * 0.20f, 0f, 1f);
                        float vSpan1 = MathHelper.Clamp(v1 - dv * 0.20f, 0f, 1f);

                        // Оставляем небольшой inset в пределах текущего пикселя
                        float uMidL = MathHelper.Clamp(u0 + du * 0.45f, 0f, 0f);
                        float uMidR = MathHelper.Clamp(u1 - du * 0.45f, 0f, 0f);
                        float vMidT = MathHelper.Clamp(v0 + dv * 0.45f, 0f, 0f);
                        float vMidB = MathHelper.Clamp(v1 - dv * 0.45f, 0f, 0f);

                        
                       
                        Color pxColor = pixels[y * w + x];
                        if (pxColor.A == 0) pxColor = Color.White;
                        
                        // Un-premultiply, чтобы убрать темные fringe-цвета
                        if (pxColor.A > 0)
                        {
                            int r = Math.Min(255, (pxColor.R * 255) / pxColor.A);
                            int g = Math.Min(255, (pxColor.G * 255) / pxColor.A);
                            int b = Math.Min(255, (pxColor.B * 255) / pxColor.A);
                            pxColor = new Color((byte)r, (byte)g, (byte)b, (byte)255);
                        }
                        else
                        {
                            pxColor = new Color((byte)255, (byte)255, (byte)255, (byte)255);
                        }

                        // LEFT side: берем левую кромку текущего пикселя
                        if (!IsOpaqueAt(x - 1, y))
                        {
                            if (!AddTexturedQuad(
                                sideVertices, sideIndices,
                                new Vector3(x0, yTop, zBack),
                                new Vector3(x0, yTop, zFront),
                                new Vector3(x0, yBottom, zBack),
                                new Vector3(x0, yBottom, zFront),
                                uInL, vSpan0, uInL, vSpan1,
                                MulOpaque(pxColor, 0.95f)))
                            {
                                return null;
                            }
                        }

                        // RIGHT side: берем правую кромку текущего пикселя
                        if (!IsOpaqueAt(x + 1, y))
                        {
                            if (!AddTexturedQuad(
                                sideVertices, sideIndices,
                                new Vector3(x1, yTop, zFront),
                                new Vector3(x1, yTop, zBack),
                                new Vector3(x1, yBottom, zFront),
                                new Vector3(x1, yBottom, zBack),
                                uInR, vSpan0, uInR, vSpan1,
                                MulOpaque(pxColor, 0.95f)))
                            {
                                return null;
                            }
                        }

                        // TOP side: берем верхнюю кромку текущего пикселя
                        if (!IsOpaqueAt(x, y - 1))
                        {
                            if (!AddTexturedQuad(
                                sideVertices, sideIndices,
                                new Vector3(x0, yTop, zBack),
                                new Vector3(x1, yTop, zBack),
                                new Vector3(x0, yTop, zFront),
                                new Vector3(x1, yTop, zFront),
                                uSpan0, vInT, uSpan1, vInT,
                                MulOpaque(pxColor, 1.00f)))
                            {
                                return null;
                            }
                        }

                        // BOTTOM side: берем нижнюю кромку текущего пикселя
                        if (!IsOpaqueAt(x, y + 1))
                        {
                            if (!AddTexturedQuad(
                                sideVertices, sideIndices,
                                new Vector3(x0, yBottom, zFront),
                                new Vector3(x1, yBottom, zFront),
                                new Vector3(x0, yBottom, zBack),
                                new Vector3(x1, yBottom, zBack),
                                uSpan0, vInB, uSpan1, vInB,
                                MulOpaque(pxColor, 0.85f)))
                            {
                                return null;
                            }
                        }
                    }
                }

                return new ExtrudedSpriteMesh
                {
                    Texture = tex,
                    SideVertices = sideVertices.ToArray(),
                    SideIndices = sideIndices.ToArray()
                };
            }
            catch
            {
                return null;
            }
        }


        private static ExtrudedSpriteMesh GetOrBuildExtrudedMesh(Texture2D tex, Rectangle srcRect, byte alphaThreshold = 16)
        {
            var key = (tex, srcRect, alphaThreshold);
            if (extrudedMeshCache.TryGetValue(key, out var mesh))
            {
                return mesh;
            }

            mesh = BuildExtrudedMeshFromAlpha(tex, srcRect, alphaThreshold);
            if (mesh != null)
            {
                extrudedMeshCache[key] = mesh;
            }
            return mesh;
        }




       
       
        
        
        private static void DrawExtrudedMesh(Limb limb, ExtrudedSpriteMesh mesh, float zCenter, float thickness, float yOffset)
        {
            if (limb?.body == null) return;

            Sprite sprite = GetLimbSprite(limb);
            if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;

            EnsureResources();
            SetupBasicEffect3D();

            var gd = GameMain.Instance.GraphicsDevice;
            Texture2D tex = sprite.Texture;
            Rectangle srcRect = sprite.SourceRect;

            float scale = limb.Scale * limb.TextureScale;
            if (scale < 0.01f) scale = 1f;

            float halfDepth = Math.Max(thickness, 16f) * 0.5f;
            float rotation = -limb.body.DrawRotation;
            Vector2 pos = new Vector2(limb.body.DrawPosition.X, limb.body.DrawPosition.Y + yOffset);

            bool facingLeft = false;
            try { facingLeft = limb.character?.AnimController?.Dir < 0; } catch { }

            bool isRightArmGroup =
                limb.type == LimbType.RightArm ||
                limb.type == LimbType.RightForearm ||
                limb.type == LimbType.RightHand;

            bool isLeftArmPart =
                limb.type == LimbType.LeftArm ||
                limb.type == LimbType.LeftForearm ||
                limb.type == LimbType.LeftHand;

            bool isRightArmPart =
                limb.type == LimbType.RightArm ||
                limb.type == LimbType.RightForearm ||
                limb.type == LimbType.RightHand;

            bool isArmPart = isLeftArmPart || isRightArmPart;

            // ---- Стабильное определение "руку реально тянут" ----
            bool IsPulledStrong(Limb l)
            {
                if (l == null || !l.PullJointEnabled) return false;
                // PullJoint anchors в sim units, небольшой порог против шума
                float d2 = Vector2.DistanceSquared(l.PullJointWorldAnchorA, l.PullJointWorldAnchorB);
                return d2 > 0.0004f;
            }

            bool armChainPulled = false;
            if (isArmPart)
            {
                try
                {
                    var limbs = limb.character?.AnimController?.Limbs;
                    if (limbs != null)
                    {
                        Limb Get(LimbType t) => limbs.FirstOrDefault(x => x?.type == t);

                        if (isLeftArmPart)
                        {
                            armChainPulled =
                                IsPulledStrong(Get(LimbType.LeftArm)) ||
                                IsPulledStrong(Get(LimbType.LeftForearm)) ||
                                IsPulledStrong(Get(LimbType.LeftHand));
                        }
                        else
                        {
                            armChainPulled =
                                IsPulledStrong(Get(LimbType.RightArm)) ||
                                IsPulledStrong(Get(LimbType.RightForearm)) ||
                                IsPulledStrong(Get(LimbType.RightHand));
                        }
                    }
                }
                catch { }
            }

            // Idle: 0°, Pull: Left=90°, Right=270°
            float targetExtraDeg = 0f;
            if (armChainPulled)
            {
                if (isLeftArmPart) targetExtraDeg = 90f;
                else if (isRightArmPart) targetExtraDeg = 270f;
            }

            // Плавный переход, чтобы убрать дергание
            if (!armExtraRotDegState.TryGetValue(limb, out float currentExtraDeg))
            {
                currentExtraDeg = targetExtraDeg;
            }
            currentExtraDeg = MathHelper.Lerp(currentExtraDeg, targetExtraDeg, 0.18f);
            armExtraRotDegState[limb] = currentExtraDeg;

            float finalRotation = rotation + MathHelper.ToRadians(currentExtraDeg);

            // Без агрессивных инверсий, чтобы не дёргало
            bool renderFacingLeft = facingLeft;

            var dsCaps = characterDepthMode == 0 ? DepthStencilState.Default : DepthStencilState.None;
            var dsSides = characterDepthMode == 0 ? DepthStencilState.Default : DepthStencilState.None;

            Color baseColor = limb.character?.Info?.Head.SkinColor ?? Color.White;
            Color frontColor = Mult(baseColor, 1.05f);
            Color backColor = Mult(baseColor, 0.72f);

            gd.RasterizerState = RasterizerState.CullNone;
            gd.SamplerStates[0] = SamplerState.PointClamp;

            float x0 = -sprite.Origin.X;
            float x1 = srcRect.Width - sprite.Origin.X;
            float y0 = sprite.Origin.Y;
            float y1 = sprite.Origin.Y - srcRect.Height;

            float texW = tex.Width;
            float texH = tex.Height;
            float uInset = 0.5f / texW;
            float vInset = 0.5f / texH;

            float u0 = srcRect.X / texW + uInset;
            float v0 = srcRect.Y / texH + vInset;
            float u1 = (srcRect.X + srcRect.Width) / texW - uInset;
            float v1 = (srcRect.Y + srcRect.Height) / texH - vInset;

            float fu0 = u0, fu1 = u1;
            if (renderFacingLeft)
            {
                float t = fu0; fu0 = fu1; fu1 = t;
            }

            float bu0 = fu1;
            float bu1 = fu0;

            Matrix capWorld =
                Matrix.CreateScale(scale, scale, 1f) *
                Matrix.CreateRotationZ(finalRotation) *
                Matrix.CreateTranslation(pos.X, pos.Y, zCenter);

            Vector3 frontTL = new Vector3(x0, y0, +halfDepth);
            Vector3 frontTR = new Vector3(x1, y0, +halfDepth);
            Vector3 frontBL = new Vector3(x0, y1, +halfDepth);
            Vector3 frontBR = new Vector3(x1, y1, +halfDepth);

            Vector3 backTL = new Vector3(x0, y0, -halfDepth);
            Vector3 backTR = new Vector3(x1, y0, -halfDepth);
            Vector3 backBL = new Vector3(x0, y1, -halfDepth);
            Vector3 backBR = new Vector3(x1, y1, -halfDepth);

            void DrawCapAlphaTest(Vector3 tl, Vector3 tr, Vector3 bl, Vector3 br, float tu0, float tv0, float tu1, float tv1, Color color)
            {
                if (alphaTestEffect == null || alphaTestEffect.IsDisposed)
                {
                    basicEffect.World = capWorld;
                    basicEffect.Texture = tex;
                    gd.DepthStencilState = dsCaps;
                    gd.BlendState = BlendState.NonPremultiplied;
                    DrawTexturedFace(gd, tex, tl, tr, bl, br, tu0, tv0, tu1, tv1, color);
                    return;
                }

                alphaTestEffect.World = capWorld;
                alphaTestEffect.View = basicEffect.View;
                alphaTestEffect.Projection = basicEffect.Projection;
                alphaTestEffect.Texture = tex;
                alphaTestEffect.VertexColorEnabled = true;
                alphaTestEffect.AlphaFunction = CompareFunction.Greater;
                alphaTestEffect.ReferenceAlpha = 180;

                gd.DepthStencilState = dsCaps;
                gd.BlendState = BlendState.Opaque;
                gd.RasterizerState = RasterizerState.CullNone;
                gd.SamplerStates[0] = SamplerState.PointClamp;

                quadVertices[0] = new VertexPositionColorTexture(tl, color, new Vector2(tu0, tv0));
                quadVertices[1] = new VertexPositionColorTexture(tr, color, new Vector2(tu1, tv0));
                quadVertices[2] = new VertexPositionColorTexture(bl, color, new Vector2(tu0, tv1));
                quadVertices[3] = new VertexPositionColorTexture(br, color, new Vector2(tu1, tv1));

                foreach (EffectPass pass in alphaTestEffect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, quadVertices, 0, 4, quadIndices, 0, 2);
                }
            }

            // 1) Back cap
            DrawCapAlphaTest(backTR, backTL, backBR, backBL, bu0, v0, bu1, v1, backColor);

            // 2) Side mesh
            if (mesh != null &&
                mesh.SideVertices != null && mesh.SideVertices.Length > 0 &&
                mesh.SideIndices != null && mesh.SideIndices.Length > 0)
            {
                float sideScaleX = (renderFacingLeft ? -1f : 1f) * scale;
                float sideScaleY = scale;

                float localOffsetX = sprite.SourceRect.Width * 0.5f - sprite.Origin.X;
                float localOffsetY = sprite.Origin.Y - sprite.SourceRect.Height * 0.5f;

                if (renderFacingLeft)
                {
                    localOffsetX = -localOffsetX;
                }

                // Во время pull слегка стабилизируем arm-chain
                if (isArmPart && armChainPulled)
                {
                    localOffsetX *= 0.35f;
                    localOffsetY = 0f;
                }

                void DrawSidePass(float offsetX, float zNudge)
                {
                    Matrix sideWorld =
                        Matrix.CreateTranslation(offsetX, localOffsetY, 0f) *
                        Matrix.CreateScale(sideScaleX, sideScaleY, Math.Max(halfDepth - 0.15f, 0.1f)) *
                        Matrix.CreateRotationZ(finalRotation) *
                        Matrix.CreateTranslation(pos.X, pos.Y, zCenter + zNudge);

                    basicEffect.World = sideWorld;
                    basicEffect.Texture = mesh.Texture;
                    gd.DepthStencilState = dsSides;
                    gd.BlendState = BlendState.Opaque;

                    foreach (EffectPass pass in basicEffect.CurrentTechnique.Passes)
                    {
                        pass.Apply();
                        gd.DrawUserIndexedPrimitives(
                            PrimitiveType.TriangleList,
                            mesh.SideVertices, 0, mesh.SideVertices.Length,
                            mesh.SideIndices, 0, mesh.SideIndices.Length / 3);
                    }
                }

                DrawSidePass(localOffsetX, 0f);

                // right double-pass отключен, чтобы не дергалось/дублилось
                // if (isRightArmGroup)
                // {
                //     DrawSidePass(-localOffsetX, 0.12f);
                // }
            }

            // 3) Front cap
            DrawCapAlphaTest(frontTL, frontTR, frontBL, frontBR, fu0, v0, fu1, v1, frontColor);

            basicEffect.World = Matrix.Identity;
            gd.DepthStencilState = DepthStencilState.Default;
        }
	
	
	
	
        private static Matrix BuildExtrudedMatrix(float zBack, float zFront)
        {
            float zCenter = (zBack + zFront) / 2f;
            float zScale = (zFront - zBack) / 2f;
            
            return Matrix.CreateScale(1, -1, zScale) * 
                   Matrix.CreateTranslation(0, 0, zCenter) * 
                   BuildViewProjection() * 
                   GetScreenMatrix();
        }
		

        private static void DrawLimbPlaneAtZ(Limb limb, float z, Color tint)
        {
            var sprite = GetLimbSprite(limb);
            if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;

            Vector2 pos = new Vector2(limb.body.DrawPosition.X, -limb.body.DrawPosition.Y);
            float rotation = -limb.body.DrawRotation;
            float scale = limb.Scale * limb.TextureScale;
            if (scale < 0.01f) scale = 1f;

            SpriteEffects effect = SpriteEffects.None;
            try
            {
                effect = limb.character?.AnimController?.Dir > 0
                    ? SpriteEffects.None
                    : SpriteEffects.FlipHorizontally;
            }
            catch { }

            // ВАЖНО: без depth-test, чтобы обе стороны гарантированно были видны
            fpBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.NonPremultiplied,
                SamplerState.PointClamp,
                DepthStencilState.None,
                RasterizerState.CullNone,
                null,
                BuildWallMatrix(z));

            sprite.Draw(fpBatch, pos, tint, sprite.Origin, rotation, new Vector2(scale, scale), effect, 0.5f);

            fpBatch.End();
        }



        private static void DrawLimb(SpriteBatch sb, Limb limb, Camera cam, float yOffset = 0f)
        {
            try
            {
                var sprite = limb.ActiveSprite;
                if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;

                Vector2 pos = new Vector2(limb.body.DrawPosition.X, -limb.body.DrawPosition.Y + yOffset);
                float rotation = -limb.body.DrawRotation;
                float scale = limb.Scale * limb.TextureScale;
                Color color = limb.character.Info?.Head.SkinColor ?? Color.White;

                SpriteEffects effect = limb.character.AnimController?.Dir > 0 ? SpriteEffects.None : SpriteEffects.FlipHorizontally;

                sprite.Draw(sb, pos, color, sprite.Origin, rotation, new Vector2(scale, scale), effect, sprite.Depth);
            }
            catch { }
        }

        private static void DrawLimbTinted(SpriteBatch sb, Limb limb, Camera cam, float tint, float yOffset = 0f)
        {
            try
            {
                var sprite = limb.ActiveSprite;
                if (sprite?.Texture == null || sprite.Texture.IsDisposed) return;

                Vector2 pos = new Vector2(limb.body.DrawPosition.X, -limb.body.DrawPosition.Y + yOffset);
                float rotation = -limb.body.DrawRotation;
                float scale = limb.Scale * limb.TextureScale;
                
                Color baseColor = limb.character.Info?.Head.SkinColor ?? Color.White;
                Color color = new Color(
                    (int)MathHelper.Clamp(baseColor.R * tint, 0, 255),
                    (int)MathHelper.Clamp(baseColor.G * tint, 0, 255),
                    (int)MathHelper.Clamp(baseColor.B * tint, 0, 255),
                    baseColor.A
                );

                SpriteEffects effect = limb.character.AnimController?.Dir > 0 ? SpriteEffects.None : SpriteEffects.FlipHorizontally;

                sprite.Draw(sb, pos, color, sprite.Origin, rotation, new Vector2(scale, scale), effect, sprite.Depth);
            }
            catch { }
        }

      
        
        private static void Draw3DWorld()
        {
            EnsureResources();
        
            Vector2 camPos = GetCameraPosition();
            var cam = Screen.Selected?.Cam;
            var gd = GameMain.Instance.GraphicsDevice;
        
            FindCurrentRoom(camPos);
            CacheStructures(camPos);
        
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
        
            DrawBackground(camPos);
        
            // Обычные плоские сущности на зад/перед стенах
            DrawWallEntities(camPos, -layerDepth, cam);
            DrawWallEntities(camPos, layerDepth, cam);
        
            // Повернутые/зеркальные структуры - один проход (без дубля)
            DrawAngledStructures(camPos, cam);
            DrawAngledStructureSides();
        
            // Обычные осевые структуры
            if (renderMode == 0)
            {
                DrawSideWallStructures(camPos);
                DrawFloorCeilingStructures(camPos);
            }
            else
            {
                DrawFloorCeilingStructures(camPos);
                DrawSideWallStructures(camPos);
            }
        
            DrawStairStructures(camPos);
            DrawDoorStructures(camPos);
			DrawDamagedStructures3D(camPos);
            DrawWater(camPos);
            DrawCharacters(cam, camPos);
            DrawDebugLookRay3D();
        }

        private static void DrawOverlay()
        {
            EnsureResources();
            int cx = GameMain.GraphicsWidth / 2, cy = GameMain.GraphicsHeight / 2;

            fpBatch.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied);

            Color col = Color.White * 0.8f;
            fpBatch.Draw(pixelTexture, new Rectangle(cx - 10, cy, 7, 2), col);
            fpBatch.Draw(pixelTexture, new Rectangle(cx + 3, cy, 7, 2), col);
            fpBatch.Draw(pixelTexture, new Rectangle(cx, cy - 10, 2, 7), col);
            fpBatch.Draw(pixelTexture, new Rectangle(cx, cy + 3, 2, 7), col);

            fpBatch.Draw(pixelTexture, new Rectangle(10, 10, 320, 100), new Color(0, 0, 0, 200));

            float charPct = (float)characterRenderMode / (CHARACTER_MODE_COUNT - 1);
            fpBatch.Draw(pixelTexture, new Rectangle(15, 83, (int)(310 * charPct), 12), Color.Magenta);

            fpBatch.Draw(pixelTexture, new Rectangle(15, 32, 310, 12), (stairRenderMode > 0 ? Color.Yellow : Color.DarkGoldenrod) * 0.5f);
            fpBatch.Draw(pixelTexture, new Rectangle(15, 49, 310, 12), (xrayMode ? Color.Lime : Color.DarkGreen) * 0.5f);

            if (showNearbyDoors && nearbyDoorsList.Count > 0)
            {
                int doorListY = 120;
                fpBatch.Draw(pixelTexture, new Rectangle(10, doorListY, 400, 20 + nearbyDoorsList.Count * 18), new Color(0, 0, 0, 220));

                int idx = 0;
                foreach (var (id, name, dist) in nearbyDoorsList)
                {
                    bool hasCustom = customDoorScales.ContainsKey(id.ToLowerInvariant());
                    Color c = hasCustom ? Color.Lime : Color.White;
                    fpBatch.Draw(pixelTexture, new Rectangle(15, doorListY + 5 + idx * 18, 8, 8), c);
                    idx++;
                }
            }

            fpBatch.End();
        }



        private static void HandleMovementInput()
        {
            var ch = Character.Controlled;
            if (ch == null || ch.AnimController == null || ch.Keys == null) return;

            var anim = ch.AnimController;

            if (flipCooldown > 0) flipCooldown--;

            if (flipFreezeFrames > 0)
            {
                flipFreezeFrames--;

                ch.Keys[(int)InputType.Left].Held = false;
                ch.Keys[(int)InputType.Right].Held = false;
                ch.Keys[(int)InputType.Up].Held = false;
                ch.Keys[(int)InputType.Down].Held = false;

                float cursorDir2 = (lockedDirection == Direction.Right) ? 1f : -1f;
                ch.CursorPosition = ch.Position + new Vector2(cursorDir2 * 500f, 0f);
                anim.TargetDir = lockedDirection;
                return;
            }

            bool lookingRight = IsLookingRight();
            Direction desiredDirection = lookingRight ? Direction.Right : Direction.Left;

            bool wDown = PlayerInput.KeyDown(Keys.W);
            bool sDown = PlayerInput.KeyDown(Keys.S);
            bool aDown = PlayerInput.KeyDown(Keys.A);
            bool dDown = PlayerInput.KeyDown(Keys.D);

            bool lookUp = IsLookingUp();
            bool lookDown = IsLookingDown();
            bool lookStraight = IsLookingStraight();

            bool nearLadder = IsNearLadder(ch);
            bool climbing = IsClimbing(ch);

            bool facingRight = (lockedDirection == Direction.Right);

            isMovingBackward = false;

            // Сброс кнопок
            ch.Keys[(int)InputType.Left].Held = false;
            ch.Keys[(int)InputType.Right].Held = false;
            ch.Keys[(int)InputType.Up].Held = false;
            ch.Keys[(int)InputType.Down].Held = false;

            // ===== ВОДА: куда смотришь, туда и плывешь =====
            bool inWaterFp = false;
            try
            {
                inWaterFp = ch.InWater || ch.AnimController?.InWater == true;
            }
            catch { }

            if (inWaterFp)
            {
                // Держим направление персонажа по yaw
                if (desiredDirection != lockedDirection)
                {
                    DoFlip(ch, desiredDirection);
                }

                facingRight = (lockedDirection == Direction.Right);

                if (wDown)
                {
                    if (lookUp)
                    {
                        ch.Keys[(int)InputType.Up].SetState(false, true);
                        ch.Keys[(int)InputType.Up].Held = true;
                    }
                    else if (lookDown)
                    {
                        ch.Keys[(int)InputType.Down].SetState(false, true);
                        ch.Keys[(int)InputType.Down].Held = true;
                    }
                    else
                    {
                        var key = facingRight ? InputType.Right : InputType.Left;
                        ch.Keys[(int)key].SetState(false, true);
                        ch.Keys[(int)key].Held = true;
                    }
                }
                else if (sDown)
                {
                    isMovingBackward = true;

                    if (lookUp)
                    {
                        ch.Keys[(int)InputType.Down].SetState(false, true);
                        ch.Keys[(int)InputType.Down].Held = true;
                    }
                    else if (lookDown)
                    {
                        ch.Keys[(int)InputType.Up].SetState(false, true);
                        ch.Keys[(int)InputType.Up].Held = true;
                    }
                    else
                    {
                        var key = facingRight ? InputType.Left : InputType.Right;
                        ch.Keys[(int)key].SetState(false, true);
                        ch.Keys[(int)key].Held = true;
                    }
                }

                float cursorDirWater = facingRight ? 1f : -1f;
                ch.CursorPosition = ch.Position + new Vector2(cursorDirWater * 500f, 0f);
                anim.TargetDir = lockedDirection;

                if (!cameraFollowMode)
                {
                    if (aDown) camOffset.X -= STRAFE_SPEED;
                    if (dDown) camOffset.X += STRAFE_SPEED;
                    if (wDown) camOffset.Y += STRAFE_SPEED;
                    if (sDown) camOffset.Y -= STRAFE_SPEED;
                }

                return;
            }

            // ===== СУША / ЛЕСТНИЦЫ =====
            if (climbing)
            {
                if (lookStraight)
                {
                    if (wDown)
                    {
                        var key = facingRight ? InputType.Right : InputType.Left;
                        ch.Keys[(int)key].SetState(false, true);
                        ch.Keys[(int)key].Held = true;
                    }
                    else if (sDown)
                    {
                        var key = facingRight ? InputType.Left : InputType.Right;
                        ch.Keys[(int)key].SetState(false, true);
                        ch.Keys[(int)key].Held = true;
                        isMovingBackward = true;
                    }
                }
                else
                {
                    if (wDown)
                    {
                        ch.Keys[(int)InputType.Up].SetState(false, true);
                        ch.Keys[(int)InputType.Up].Held = true;
                    }
                    else if (sDown)
                    {
                        ch.Keys[(int)InputType.Down].SetState(false, true);
                        ch.Keys[(int)InputType.Down].Held = true;
                    }
                }
            }
            else if (nearLadder && wDown && lookUp)
            {
                ch.Keys[(int)InputType.Up].SetState(false, true);
                ch.Keys[(int)InputType.Up].Held = true;

                var ladder = FindNearestLadder(ch);
                if (ladder != null)
                {
                    try { ladder.Select(ch); } catch { }
                }
            }
            else if (nearLadder && sDown && lookDown)
            {
                ch.Keys[(int)InputType.Down].SetState(false, true);
                ch.Keys[(int)InputType.Down].Held = true;

                var ladder = FindNearestLadder(ch);
                if (ladder != null)
                {
                    try { ladder.Select(ch); } catch { }
                }
            }
            else
            {
                if (wDown)
                {
                    var key = facingRight ? InputType.Right : InputType.Left;
                    ch.Keys[(int)key].SetState(false, true);
                    ch.Keys[(int)key].Held = true;
                }
                else if (sDown)
                {
                    var key = facingRight ? InputType.Left : InputType.Right;
                    ch.Keys[(int)key].SetState(false, true);
                    ch.Keys[(int)key].Held = true;
                    isMovingBackward = true;
                }
            }

            float cursorDir = facingRight ? 1f : -1f;
            ch.CursorPosition = ch.Position + new Vector2(cursorDir * 500f, 0f);

            if (!climbing && !isMovingBackward && desiredDirection != lockedDirection)
            {
                DoFlip(ch, desiredDirection);
            }

            anim.TargetDir = lockedDirection;

            if (!cameraFollowMode)
            {
                if (aDown) camOffset.X -= STRAFE_SPEED;
                if (dDown) camOffset.X += STRAFE_SPEED;
                if (wDown) camOffset.Y += STRAFE_SPEED;
                if (sDown) camOffset.Y -= STRAFE_SPEED;
            }
        }



        private static void OnUpdate()
        {
            // NumPad7 — переключение режима отображения частей тела
            if (PlayerInput.KeyHit(Keys.NumPad7))
            {
                limbDisplayMode = (limbDisplayMode + 1) % LIMB_DISPLAY_MODE_COUNT;
                selectedSubPart = 0;
                selectedFace = 0;
                DebugConsole.NewMessage($"Limb Display [{limbDisplayMode}]: {GetLimbDisplayModeName(limbDisplayMode)}", Color.Cyan);
                if (limbDisplayMode > 0)
                {
                    DebugConsole.NewMessage($"  NumPad8=подчасть, NumPad4=грань, +/-=offset, *|/=толщина, NumPad6=грань offset", Color.Gray);
                }
            }
            if (PlayerInput.KeyHit(Keys.N))
            {
                showLookRay = !showLookRay;
                DebugConsole.NewMessage("Look Ray: " + (showLookRay ? "ON" : "OFF"), showLookRay ? Color.Lime : Color.Orange);
            }
            // L: выбор nearest shell target / Shift+L: старый angled mode
            if (PlayerInput.KeyHit(Keys.L))
            {
                bool shiftHeld = PlayerInput.KeyDown(Keys.LeftShift) || PlayerInput.KeyDown(Keys.RightShift);

                if (shiftHeld)
                {
                    angledMode = (angledMode + 1) % ANGLED_MODE_COUNT;
                    DebugConsole.NewMessage($"Angled Mode [{angledMode}]: {GetAngledModeName(angledMode)}", Color.Cyan);
                    LogAngledDebugInfo();
                }
                else
                {
                    Vector2 refPos = GetCameraPosition();
                    var nearest = FindNearestShellLikeStructure(refPos);

                    if (nearest == null)
                    {
                        DebugConsole.NewMessage("ShellDebug: nearest shell/angled structure not found", Color.Orange);
                    }
                    else
                    {
                        debugShellTarget = nearest;
                        debugShellTargetId = nearest.ID;
                        debugShellTargetMirrorInvert = false;
                        LogShellDebugInfo(debugShellTarget, "ShellPick");
                    }
                }
            }

            // K: toggle mirror только для выбранной структуры / Shift+K: floor-ceiling mode
            if (PlayerInput.KeyHit(Keys.K))
            {
                bool shiftHeld = PlayerInput.KeyDown(Keys.LeftShift) || PlayerInput.KeyDown(Keys.RightShift);

                if (shiftHeld)
                {
                    floorCeilingTestMode = (floorCeilingTestMode + 1) % FLOOR_CEILING_TEST_MODE_COUNT;
                    DebugConsole.NewMessage($"Floor/Ceiling Mode [{floorCeilingTestMode}]: {GetFloorCeilingTestModeName(floorCeilingTestMode)}", Color.Yellow);
                }
                else
                {
                    if (debugShellTarget == null || debugShellTarget.Removed)
                    {
                        DebugConsole.NewMessage("ShellDebug: target not selected. Press L first.", Color.Orange);
                    }
                    else
                    {
                        debugShellTargetMirrorInvert = !debugShellTargetMirrorInvert;
                        DebugConsole.NewMessage(
                            $"ShellDebug: mirror invert for selected target = {(debugShellTargetMirrorInvert ? "ON" : "OFF")}",
                            debugShellTargetMirrorInvert ? Color.Lime : Color.Orange);
                        LogShellDebugInfo(debugShellTarget, "ShellToggle");
                    }
                }
            }

            // F8 — дебаг размеров
            if (PlayerInput.KeyHit(Keys.F8))
            {
                Vector2 camPos = GetCameraPosition();
                Character nearest = null;
                float minDist = float.MaxValue;

                foreach (var c in Character.CharacterList)
                {
                    if (c == Character.Controlled) continue;
                    float dist = Vector2.Distance(camPos, c.WorldPosition);
                    if (dist < minDist) { minDist = dist; nearest = c; }
                }

                if (nearest?.AnimController?.Limbs != null)
                {
                    DebugConsole.NewMessage($"=== {nearest.SpeciesName} SCALE INFO ===", Color.Yellow);
                    for (int i = 0; i < nearest.AnimController.Limbs.Length; i++)
                    {
                        var limb = nearest.AnimController.Limbs[i];
                        if (limb == null) continue;

                        float limbScale = limb.Scale;
                        float textureScale = limb.TextureScale;
                        float combined = limbScale * textureScale;

                        Rectangle srcRect = Rectangle.Empty;
                        if (limb.DeformSprite?.Sprite != null) srcRect = limb.DeformSprite.Sprite.SourceRect;
                        else if (limb.ActiveSprite != null) srcRect = limb.ActiveSprite.SourceRect;

                        float finalW = srcRect.Width * combined;
                        float finalH = srcRect.Height * combined;

                        DebugConsole.NewMessage(
                            $"[{i}] {limb.type}: Scale={limbScale:F3} TexScale={textureScale:F3} Combined={combined:F3} => {finalW:F1}x{finalH:F1}",
                            combined < 0.1f ? Color.Red : Color.White);
                    }
                }
            }

            if (PlayerInput.KeyHit(Keys.F9))
            {
                showLimbDebug = !showLimbDebug;
                DebugConsole.NewMessage($"Limb Debug: {(showLimbDebug ? "ON" : "OFF")}", showLimbDebug ? Color.Lime : Color.Orange);
            }

            if (PlayerInput.KeyHit(Keys.F11))
            {
                Vector2 camPos = GetCameraPosition();
                Character nearest = null;
                float minDist = float.MaxValue;

                foreach (var c in Character.CharacterList)
                {
                    if (c == Character.Controlled) continue;
                    float dist = Vector2.Distance(camPos, c.WorldPosition);
                    if (dist < minDist) { minDist = dist; nearest = c; }
                }

                if (nearest?.AnimController?.Limbs != null)
                {
                    DebugConsole.NewMessage($"=== {nearest.SpeciesName} POSITION INFO ===", Color.Yellow);
                    DebugConsole.NewMessage($"Character WorldPos: {nearest.WorldPosition}", Color.Cyan);
                    DebugConsole.NewMessage($"Camera Pos: {camPos}", Color.Cyan);

                    for (int i = 0; i < Math.Min(nearest.AnimController.Limbs.Length, 5); i++)
                    {
                        var limb = nearest.AnimController.Limbs[i];
                        if (limb == null) continue;
                        Vector2 drawPos = limb.body.DrawPosition;
                        Vector2 worldPos = limb.WorldPosition;

                        DebugConsole.NewMessage(
                            $"[{i}] {limb.type}: DrawPos=({drawPos.X:F0},{drawPos.Y:F0}) WorldPos=({worldPos.X:F0},{worldPos.Y:F0})",
                            Color.White);
                    }
                }
            }

            if (PlayerInput.KeyHit(Keys.F1))
            {
                stretchMethod = (stretchMethod + 1) % STRETCH_METHOD_COUNT;
                string methodName = stretchMethod switch
                {
                    0 => "ActiveSprite only",
                    1 => "DeformSprite.Sprite only",
                    2 => "Sprite only",
                    3 => "Active -> Deform -> Sprite",
                    4 => "Deform -> Active -> Sprite",
                    5 => "Sprite -> Deform -> Active",
                    6 => "Reflection _deformSprite",
                    7 => "ConditionalSprites first",
                    8 => "GetActiveSprite(false)",
                    9 => "GetActiveSprite(true)",
                    _ => "Unknown"
                };
                DebugConsole.NewMessage($"Stretch Method [{stretchMethod}]: {methodName}", Color.Yellow);
            }

            if (PlayerInput.KeyHit(Keys.F4))
            {
                spriteDebugMode = (spriteDebugMode + 1) % SPRITE_DEBUG_MODE_COUNT;
                string methodName = spriteDebugMode switch
                {
                    0 => "DeformSprite direct",
                    1 => "ActiveSprite",
                    2 => "Sprite direct",
                    3 => "Reflection _deformSprite",
                    4 => "DeformSprite + Size",
                    5 => "DeformSprite -> Sprite combo",
                    6 => "ActiveSprite + body size",
                    7 => "GetActiveSprite(true)",
                    _ => "Unknown"
                };
                DebugConsole.NewMessage($"Sprite Debug Mode [{spriteDebugMode}]: {methodName}", Color.Cyan);
            }

            if (PlayerInput.KeyHit(Keys.F6))
            {
                Vector2 camPos = GetCameraPosition();
                Character nearest = null;
                float minDist = float.MaxValue;

                foreach (var c in Character.CharacterList)
                {
                    if (c == Character.Controlled) continue;
                    float dist = Vector2.Distance(camPos, c.WorldPosition);
                    if (dist < minDist) { minDist = dist; nearest = c; }
                }

                if (nearest?.AnimController?.Limbs != null)
                {
                    DebugConsole.NewMessage($"=== {nearest.SpeciesName} LIMBS INFO ===", Color.Yellow);
                    for (int i = 0; i < nearest.AnimController.Limbs.Length; i++)
                    {
                        var limb = nearest.AnimController.Limbs[i];
                        if (limb == null) continue;

                        string info = $"[{i}] {limb.type}: ";

                        if (limb.DeformSprite?.Sprite != null) info += $"DeformSprite={limb.DeformSprite.Sprite.SourceRect.Width}x{limb.DeformSprite.Sprite.SourceRect.Height} ";
                        else info += "DeformSprite=NULL ";

                        if (limb.Sprite != null) info += $"Sprite={limb.Sprite.SourceRect.Width}x{limb.Sprite.SourceRect.Height} ";
                        else info += "Sprite=NULL ";

                        if (limb.ActiveSprite != null) info += $"Active={limb.ActiveSprite.SourceRect.Width}x{limb.ActiveSprite.SourceRect.Height}";
                        else info += "Active=NULL";

                        DebugConsole.NewMessage(info, Color.White);
                    }
                }
                else
                {
                    DebugConsole.NewMessage("No NPC nearby", Color.Red);
                }
            }

            if (PlayerInput.KeyHit(Keys.NumPad0))
            {
                texturePadding += 0.05f;
                if (texturePadding > 0.5f) texturePadding = 0f;
                DebugConsole.NewMessage($"Texture Padding: {texturePadding:P0} (фон меньше на {texturePadding:P0})", Color.Lime);
            }

            if (PlayerInput.KeyHit(Keys.NumPad8))
            {
                int subPartCount = GetSubPartCount(limbDisplayMode);
                if (subPartCount > 0)
                {
                    selectedSubPart = (selectedSubPart + 1) % subPartCount;
                    selectedFace = 0;
                    string partName = GetSubPartName(limbDisplayMode, selectedSubPart);
                    float offset = GetSelectedOffset();
                    float thickness = GetSelectedThickness();
                    DebugConsole.NewMessage($"SubPart [{selectedSubPart}]: {partName} | offset={offset:F1}, thickness={thickness:F1}", Color.Yellow);
                }
                else
                {
                    DebugConsole.NewMessage("Сначала выберите группу (NumPad7)", Color.Orange);
                }
            }

            if (PlayerInput.KeyHit(Keys.NumPad4))
            {
                if (limbDisplayMode > 0)
                {
                    selectedFace = (selectedFace + 1) % FACE_COUNT;
                    string partName = GetSubPartName(limbDisplayMode, selectedSubPart);
                    string faceName = GetFaceName(selectedFace);
                    float faceOffset = GetSelectedFaceOffset();
                    DebugConsole.NewMessage($"{partName} -> Face [{selectedFace}]: {faceName} | offset={faceOffset:F1}", Color.Magenta);
                }
            }

            if (PlayerInput.KeyHit(Keys.NumPad6))
            {
                if (limbDisplayMode > 0)
                {
                    float current = GetSelectedFaceOffset();
                    float newValue = current + 1f;
                    SetSelectedFaceOffset(newValue);
                    DebugConsole.NewMessage($"{GetSubPartName(limbDisplayMode, selectedSubPart)} -> {GetFaceName(selectedFace)}: offset = {newValue:F1}", Color.Lime);
                }
            }

            if (PlayerInput.KeyHit(Keys.NumPad5))
            {
                if (limbDisplayMode > 0)
                {
                    float current = GetSelectedFaceOffset();
                    float newValue = current - 1f;
                    SetSelectedFaceOffset(newValue);
                    DebugConsole.NewMessage($"{GetSubPartName(limbDisplayMode, selectedSubPart)} -> {GetFaceName(selectedFace)}: offset = {newValue:F1}", Color.Lime);
                }
            }

            if (PlayerInput.KeyHit(Keys.NumPad2))
            {
                characterDepthMode = (characterDepthMode + 1) % 2;
                string modeName = characterDepthMode == 0 ? "NORMAL (с глубиной)" : "XRAY (видеть сквозь)";
                DebugConsole.NewMessage($"Character Depth Mode: {modeName}", Color.Yellow);
            }

            if (PlayerInput.KeyHit(Keys.Add) && limbDisplayMode > 0)
            {
                float newValue = GetSelectedOffset() + 2f;
                SetSelectedOffset(newValue);
                DebugConsole.NewMessage($"{GetSubPartName(limbDisplayMode, selectedSubPart)}: offset = {newValue:F1}", Color.Lime);
            }

            if (PlayerInput.KeyHit(Keys.Subtract) && limbDisplayMode > 0)
            {
                float newValue = GetSelectedOffset() - 2f;
                SetSelectedOffset(newValue);
                DebugConsole.NewMessage($"{GetSubPartName(limbDisplayMode, selectedSubPart)}: offset = {newValue:F1}", Color.Lime);
            }

            if (PlayerInput.KeyHit(Keys.OemPlus) && limbDisplayMode > 0)
            {
                var (scX, scY) = GetSelectedFaceScale();
                float newValue = scX + 0.1f;
                SetSelectedFaceScaleX(newValue);
                DebugConsole.NewMessage($"{GetSubPartName(limbDisplayMode, selectedSubPart)} -> {GetFaceName(selectedFace)}: scaleX = {newValue:F2}", Color.Magenta);
            }

            if (PlayerInput.KeyHit(Keys.OemMinus) && limbDisplayMode > 0)
            {
                var (scX, scY) = GetSelectedFaceScale();
                float newValue = scX - 0.1f;
                SetSelectedFaceScaleX(newValue);
                DebugConsole.NewMessage($"{GetSubPartName(limbDisplayMode, selectedSubPart)} -> {GetFaceName(selectedFace)}: scaleX = {newValue:F2}", Color.Magenta);
            }

            if (PlayerInput.KeyHit(Keys.Multiply) && limbDisplayMode > 0)
            {
                var (scX, scY) = GetSelectedFaceScale();
                float newValue = scY + 0.1f;
                SetSelectedFaceScaleY(newValue);
                DebugConsole.NewMessage($"{GetSubPartName(limbDisplayMode, selectedSubPart)} -> {GetFaceName(selectedFace)}: scaleY = {newValue:F2}", Color.Cyan);
            }

            if (PlayerInput.KeyHit(Keys.Divide) && limbDisplayMode > 0)
            {
                var (scX, scY) = GetSelectedFaceScale();
                float newValue = scY - 0.1f;
                SetSelectedFaceScaleY(newValue);
                DebugConsole.NewMessage($"{GetSubPartName(limbDisplayMode, selectedSubPart)} -> {GetFaceName(selectedFace)}: scaleY = {newValue:F2}", Color.Cyan);
            }

            if (PlayerInput.KeyHit(Keys.NumPad3))
            {
                characterRenderMode = (characterRenderMode + 1) % CHARACTER_MODE_COUNT;
                var p = characterModeParams[characterRenderMode];
                string info = characterRenderMode == 0 ? "" : $" (H:{p.Item1} T:{p.Item2} A:{p.Item3}±{p.Item4} L:{p.Item5}±{p.Item6})";
                DebugConsole.NewMessage($"Character [{characterRenderMode}]: {GetCharacterModeName(characterRenderMode)}{info}", Color.Magenta);
            }

            if (PlayerInput.KeyHit(Keys.NumPad1))
            {
                limbBackgroundMode = (limbBackgroundMode + 1) % LIMB_BG_MODE_COUNT;
                textureBoundsCache.Clear();
                string modeName = limbBackgroundMode switch
                {
                    0 => "TRANSPARENT (прозрачный)",
                    1 => "WHITE (белый в границах текстуры)",
                    2 => "BLACK (чёрный в границах текстуры)",
                    3 => "SKIN (цвет кожи в границах текстуры)",
                    _ => "Unknown"
                };
                DebugConsole.NewMessage($"Limb Background: {modeName}", Color.Yellow);
            }

            if (PlayerInput.KeyHit(Keys.F2))
            {
                xrayMode = !xrayMode;
                DebugConsole.NewMessage("X-Ray: " + (xrayMode ? "ON" : "OFF"), xrayMode ? Color.Lime : Color.Orange);
            }

            if (PlayerInput.KeyHit(Keys.F5))
            {
                isFirstPerson = !isFirstPerson;
                if (isFirstPerson)
                {
                    if (Character.Controlled == null) { isFirstPerson = false; return; }

                    EnsureResources();

                    float charDir = Character.Controlled.AnimController?.Dir ?? 1f;
                    camYaw = charDir > 0 ? 0f : MathF.PI;
                    camPitch = 0f;

                    lockedDirection = charDir > 0 ? Direction.Right : Direction.Left;
                    isMovingBackward = false;
                    flipCooldown = 0;
                    flipFreezeFrames = 0;

                    EnterFirstPerson();
                    Mouse.SetPosition(GameMain.GraphicsWidth / 2, GameMain.GraphicsHeight / 2);

                    DebugConsole.NewMessage($"3D ON | NumPad3=CharMode ({GetCharacterModeName(characterRenderMode)})", Color.LimeGreen);
                }
                else
                {
                    ExitFirstPerson();
                    DebugConsole.NewMessage("3D: OFF", Color.Orange);
                }
            }

            if (PlayerInput.KeyHit(Keys.F7))
            {
                cameraFollowMode = !cameraFollowMode;
                camOffset = Vector2.Zero;
                DebugConsole.NewMessage("Camera follow: " + (cameraFollowMode ? "ON" : "OFF"), cameraFollowMode ? Color.Lime : Color.Orange);
            }

            if (PlayerInput.KeyHit(Keys.F10))
            {
                show2DWindow = !show2DWindow;
                DebugConsole.NewMessage("Debug: " + (show2DWindow ? "ON" : "OFF"), show2DWindow ? Color.LimeGreen : Color.Orange);
            }

            // Раньше тут был renderMode на F1 — оставил выключенным, чтобы не конфликтовал со stretchMethod
            // if (PlayerInput.KeyHit(Keys.F1)) renderMode = (renderMode + 1) % RENDER_MODE_COUNT;

            if (!isFirstPerson || Character.Controlled == null) return;

            foreach (var c in Character.CharacterList)
            {
                ApplyArmPullAnchorOverrides(c);
            }

            try
            {
                GameMain.LightManager.LosEnabled = false;
                GameMain.LightManager.LightingEnabled = false;
            }
            catch { }

            var ms = Mouse.GetState();
            int mcx = GameMain.GraphicsWidth / 2, mcy = GameMain.GraphicsHeight / 2;

            camYaw += (ms.X - mcx) * MOUSE_SENSITIVITY;
            camYaw %= MathF.PI * 2f;
            if (camYaw < 0) camYaw += MathF.PI * 2f;

            camPitch = MathHelper.Clamp(
                camPitch - (ms.Y - mcy) * MOUSE_SENSITIVITY * 0.7f,
                -MathF.PI / 2f + 0.1f,
                MathF.PI / 2f - 0.1f);

            Mouse.SetPosition(mcx, mcy);

            HandleMovementInput();

            // ВАЖНО: look-based курсор для интеракта/hover
            UpdateFirstPersonCursorTarget();
            bool interactPressed = PlayerInput.KeyHit(Keys.E) || IsLeftMouseClicked();
            if (interactPressed)
            {
                if (fpLookTarget != null)
                {
                    string tid = "unknown";
                    try
                    {
                        if (fpLookTarget is Item it) tid = "Item:" + (it.Prefab?.Identifier.ToString() ?? it.Name);
                        else if (fpLookTarget is Structure st) tid = "Structure:" + (st.Prefab?.Identifier.ToString() ?? st.Name);
                    }
                    catch { }
                    DebugConsole.NewMessage("FP Click target -> " + tid, Color.Cyan);
                }
                else
                {
                    DebugConsole.NewMessage("FP Click target -> NONE", Color.Orange);
                }

                TryInteractWithLookTarget();
            }

            var cam = Screen.Selected?.Cam;
            if (cam != null)
            {
                cam.Position = GetCameraPosition();
                cam.Zoom = 0.01f;
                cam.Rotation = 0f;
            }
        }




        private static void OverrideTransform(Camera cam)
        {
            if (!isFirstPerson || transformField == null) return;
            Matrix m = BuildWallMatrix(0f);
            transformField.SetValue(cam, m);
            if (shaderTransformField != null) shaderTransformField.SetValue(cam, m);
        }
    }
}