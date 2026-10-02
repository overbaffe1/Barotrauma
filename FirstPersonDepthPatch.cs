// ============================================================================
//  FirstPersonDepthPatch.cs — Sprite.Depth → Z (частичный класс к v2)
//
//  ЧТО ДЕЛАЕТ:
//  Заменяет фиксированные ±layerDepth слои на НАСТОЯЩУЮ Z-координату из
//  редакторского параметра SpriteDepth (0.001..0.999 — тот самый слайдер
//  в редакторе субов). Каждая structure/item попадает в одну из N полос
//  глубины внутри ±layerDepth, дальние рисуются раньше и слегка темнее.
//  Параллакс становится непрерывным: детали из редактора автоматически
//  раскладываются по глубине без ручной раскладки.
//
//  ВКЛЮЧЕНИЕ: NumPad9 в режиме FP (v2). По умолчанию OFF — старый рендер.
//
<<<<<<< HEAD
//  ТРЕБОВАНИЯ К ГЛАВНОМУ ФАЙЛУ (FirstPersonMod.cs (2).cs) — УЖЕ ВНЕСЕНЫ:
//  1) public partial class FirstPersonCamera : ACsMod
//  2) В Draw3DWorld():
=======
//  ТРЕБОВАНИЯ К ГЛАВНОМУ ФАЙЛУ (FirstPersonMod.cs (2).cs):
//  1) class FirstPersonCamera → public partial class FirstPersonCamera : ACsMod
//  2) В Draw3DWorld() вместо двух DrawWallEntities:
>>>>>>> 36f55e7 (Wave 473: implemented FirstPersonDepthPatch.cs — Sprite.Depth->Z for user's FP mod (partial class, 16 depth bands from editor's SpriteDepth slider, NumPad9 toggle, far-band fade via SpriteColor save/restore); v2 file patched: partial class + Draw3DWorld branch + toggle; awaiting user compile/test)
//         if (UseEditorDepth)
//         {
//             DrawWallEntitiesEditorDepth(camPos, cam);
//         }
//         else
//         {
//             DrawWallEntities(camPos, -layerDepth, cam);
//             DrawWallEntities(camPos, layerDepth, cam);
//         }
<<<<<<< HEAD
//  3) Тоггл NumPad9 рядом с F2 X-Ray.
=======
//  3) Тоггл (рядом с остальными KeyHit):
//         if (PlayerInput.KeyHit(Keys.NumPad9))
//         {
//             UseEditorDepth = !UseEditorDepth;
//             DebugConsole.NewMessage("EditorDepth Z: " + (UseEditorDepth ? "ON" : "OFF"),
//                 UseEditorDepth ? Color.LimeGreen : Color.Orange);
//         }
>>>>>>> 36f55e7 (Wave 473: implemented FirstPersonDepthPatch.cs — Sprite.Depth->Z for user's FP mod (partial class, 16 depth bands from editor's SpriteDepth slider, NumPad9 toggle, far-band fade via SpriteColor save/restore); v2 file patched: partial class + Draw3DWorld branch + toggle; awaiting user compile/test)
// ============================================================================

using System.Collections.Generic;
using Barotrauma;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace FirstPersonMod
{
<<<<<<< HEAD
    // Base type повторён намеренно — partial-части должны совпадать.
=======
    // ВАЖНО: главный файл должен стать 'public partial class FirstPersonCamera : ACsMod'
>>>>>>> 36f55e7 (Wave 473: implemented FirstPersonDepthPatch.cs — Sprite.Depth->Z for user's FP mod (partial class, 16 depth bands from editor's SpriteDepth slider, NumPad9 toggle, far-band fade via SpriteColor save/restore); v2 file patched: partial class + Draw3DWorld branch + toggle; awaiting user compile/test)
    public partial class FirstPersonCamera : ACsMod
    {
        // ===== НАСТРОЙКИ ГЛУБИНЫ =====
        public static bool UseEditorDepth = false;

        /// <summary>Полос глубины (Begin/End на полосу). 16 = незаметный оверхед.</summary>
        private const int EditorDepthBands = 16;

        /// <summary>Насколько темнеют дальние полосы (0.55 = дальние на 45% темнее).</summary>
        private const float EditorDepthFadeMin = 0.55f;

<<<<<<< HEAD
        /// <summary>Границы SpriteDepth из редактора.</summary>
=======
        /// <summary>Минимальная SpriteDepth (в редакторе слайдер от 0.001).</summary>
>>>>>>> 36f55e7 (Wave 473: implemented FirstPersonDepthPatch.cs — Sprite.Depth->Z for user's FP mod (partial class, 16 depth bands from editor's SpriteDepth slider, NumPad9 toggle, far-band fade via SpriteColor save/restore); v2 file patched: partial class + Draw3DWorld branch + toggle; awaiting user compile/test)
        private const float MinSpriteDepth = 0.001f;
        private const float MaxSpriteDepth = 0.999f;

        // ===== Z ИЗ РЕДАКТОРСКОЙ ГЛУБИНЫ =====

        /// <summary>
        /// SpriteDepth 0.001..0.999 → −layerDepth..+layerDepth.
        /// 0.5 = экранная плоскость (z=0), меньше = дальше, больше = ближе.
        /// </summary>
        private static float GetEntityEditorDepthZ(MapEntity e)
        {
            float d = MathHelper.Clamp(e.SpriteDepth, MinSpriteDepth, MaxSpriteDepth);
            return (d - 0.5f) * 2f * layerDepth;
        }

        private static int DepthToBand(float z)
        {
            float t = (z + layerDepth) / (2f * layerDepth);   // 0..1
            return MathHelper.Clamp((int)(t * EditorDepthBands), 0, EditorDepthBands - 1);
        }

        private static float BandToZ(int band)
        {
            float t = (band + 0.5f) / EditorDepthBands;       // центр полосы
            return (t - 0.5f) * 2f * layerDepth;
        }

        // ===== ОСНОВНОЙ ПРОХОД =====

        /// <summary>
        /// Замена пары DrawWallEntities(±layerDepth): все structure/item
        /// распределяются по полосам редакторской глубины и рисуются
        /// дальние→ближние. Двери/лестницы/angled продолжают идти
        /// своими отдельными проходами (как в оригинальном Draw3DWorld).
        /// </summary>
        private static void DrawWallEntitiesEditorDepth(Vector2 camPos, Camera cam)
        {
            // --- 1. Раскидываем видимые сущности по бакетам ---
            var buckets = new List<MapEntity>[EditorDepthBands];
            for (int i = 0; i < EditorDepthBands; i++) { buckets[i] = new List<MapEntity>(); }

            foreach (var entity in MapEntity.MapEntityList)
            {
                try
                {
                    // те же фильтры, что в DrawWallEntities:
                    // angled/special shell идут отдельным angled-проходом
                    bool drawable;
                    float z;

                    if (entity is Structure st &&
                        st.HasBody && st.Submarine != null &&
                        !IsAngledStructure(st) && !IsSpecialAngledShell(st) &&
                        ShouldDrawEntity(st, camPos))
                    {
                        drawable = true;
                        z = GetEntityEditorDepthZ(st);
                    }
                    else if (entity is Item item &&
                             (item.ParentInventory == null || item.ParentInventory is CharacterInventory) &&
                             ShouldDrawEntity(item, camPos))
                    {
                        drawable = true;
                        z = GetEntityEditorDepthZ(item);
                    }
                    else
                    {
                        continue;
                    }

                    if (!drawable) { continue; }

                    z = MathHelper.Clamp(z, -layerDepth, layerDepth);
                    buckets[DepthToBand(z)].Add(entity);
                }
                catch { }
            }

            // --- 2. Рисуем полосы: дальние → ближние ---
<<<<<<< HEAD
=======
            var gd = GameMain.Instance.GraphicsDevice;

>>>>>>> 36f55e7 (Wave 473: implemented FirstPersonDepthPatch.cs — Sprite.Depth->Z for user's FP mod (partial class, 16 depth bands from editor's SpriteDepth slider, NumPad9 toggle, far-band fade via SpriteColor save/restore); v2 file patched: partial class + Draw3DWorld branch + toggle; awaiting user compile/test)
            for (int band = 0; band < EditorDepthBands; band++)
            {
                if (buckets[band].Count == 0) { continue; }

                float z = BandToZ(band);
                float fade = MathHelper.Lerp(EditorDepthFadeMin, 1f, (band + 1f) / EditorDepthBands);
                var tint = new Color(fade, fade, fade);

                fpBatch.Begin(
                    SpriteSortMode.BackToFront,
                    BlendState.NonPremultiplied,
                    SamplerState.LinearWrap,
                    null,
                    RasterizerState.CullNone,
                    null,
                    BuildWallMatrix(z));

                foreach (var entity in buckets[band])
                {
                    try
                    {
                        if (entity is Structure st)
                        {
                            // Structure.Draw не принимает цвет — временно
                            // подменяем SpriteColor и восстанавливаем
                            Color saved = st.SpriteColor;
                            var faded = new Color(
                                (int)(saved.R * fade),
                                (int)(saved.G * fade),
                                (int)(saved.B * fade));
                            st.SpriteColor = faded;
                            try
                            {
                                st.Draw(fpBatch, false, true);
                                st.Draw(fpBatch, false, false);
                            }
                            finally
                            {
                                st.SpriteColor = saved;
                            }
                        }
                        else if (entity is Item item)
                        {
                            item.Draw(fpBatch, false, true, tint);
                            item.Draw(fpBatch, false, false, tint);
                        }
                    }
                    catch { }
                }

                fpBatch.End();
            }
<<<<<<< HEAD
        }

        // ===== ОПЦИОНАЛЬНО: отладочная гистограмма =====

        /// <summary>
        /// Лог распределения сущностей по полосам (раз в секунду).
=======

            // gd не используется напрямую, оставлен для будущих depth-RT проходов
            _ = gd;
        }

        // ===== ОПЦИОНАЛЬНО: мягкая "фоторамка" для debugging =====

        /// <summary>
        /// Отладка: лог распределения сущностей по полосам (раз в секунду).
>>>>>>> 36f55e7 (Wave 473: implemented FirstPersonDepthPatch.cs — Sprite.Depth->Z for user's FP mod (partial class, 16 depth bands from editor's SpriteDepth slider, NumPad9 toggle, far-band fade via SpriteColor save/restore); v2 file patched: partial class + Draw3DWorld branch + toggle; awaiting user compile/test)
        /// Вызывать из think-хука при UseEditorDepth == true.
        /// </summary>
        private static double _lastDepthLog;
        internal static void DebugLogEditorDepthBands()
        {
            double now = Timing.TotalTime;
            if (now - _lastDepthLog < 1.0) { return; }
            _lastDepthLog = now;

            int[] counts = new int[EditorDepthBands];
            foreach (var entity in MapEntity.MapEntityList)
            {
                try
                {
                    bool drawable = (entity is Structure st && st.HasBody && st.Submarine != null) ||
                                    (entity is Item it && it.ParentInventory == null);
                    if (!drawable) { continue; }
                    counts[DepthToBand(GetEntityEditorDepthZ(entity))]++;
                }
                catch { }
            }

            string bars = "";
            for (int i = 0; i < EditorDepthBands; i++)
            {
                bars += counts[i] > 0 ? counts[i].ToString("X") : ".";
            }
            DebugConsole.NewMessage("[EditorDepth] bands (far→near): " + bars, Color.Cyan);
        }
    }
}
