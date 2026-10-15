// ============================================================================
//  BiomeJumpModule.cs — произвольный прыжок по карте кампании (волна 703)
//
//  SelectLocation(selectedLocIndex) в MultiPlayerCampaign.ServerRead
//  НЕ проверяет AllowDebugTeleport, соседность, разблокировку биома,
//  репутацию — ТОЛЬКО AllowedToManageCampaign(ManageMap) (фоллбэк 688b).
//
//  Пакет SERVER_COMMAND + ManageCampaign несёт:
//    u16 currentLocIndex
//    u16 selectedLocIndex  ← ЛЮБОЙ индекс 0..Locations.Count-1
//    byte missionCount + N×byte
//    bool hullRepairs / itemRepairs / shuttles
//    buyCrate / subSellCrate / purchased / sold (4 блока)
//    u16 upgrades
//    u16 swaps
//
//  ЧТО ЭТО ДАЁТ:
//  - Прыжок в эндгейм-биом (последний индекс) с начала кампании
//  - Выбор радиационной локации (облучение экипажа)
//  - Выбор враждебного аутпоста (NPC агрессивны)
//  - Выбор пустой локации (нет магазина/найма)
//
//  Список локаций синкнут клиенту (NetFlags.MapAndMissions) — читаем
//  GameMain.GameSession.Campaign.Map.Locations напрямую.
//
//  Дополнительно: кнопка «SetLocation» (телепорт ТЕКУЩЕЙ точки карты)
//  работает только если сервер включил AllowDebugTeleport — пробуем, если
//  нет — всё равно увидим в статусе.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class BiomeJumpModule : CSModuleBase
    {
        public override string Id   => "biome_jump";
        public override string Name => "Biome Jump";
        public override string Description =>
            "Произвольный прыжок по карте кампании: SelectLocation(selectedLocIndex) не проверяет " +
            "AllowDebugTeleport/соседность/разблокировку. Эндгейм, радиация, враждебные аутпосты — " +
            "любая локация по индексу. Требует ManageMap фоллбэк.";
        public override string Category => "exploit";

        public override string GetLabel() => "Biome Jump 🗺";

        private static GUIMessageBox _window;
        private static GUIListBox _locList;
        private static GUITextBlock _status;
        private static int _selectedIdx = -1;
        private static string _selectedName = "";

        public override void OnClick()
        {
            try
            {
                if (_window != null) { CloseWindow(); return; }
                OpenWindow();
            }
            catch (Exception e) { LuaCsLogger.LogError("[BiomeJump] " + e.Message); }
        }

        public override void Dispose() { CloseWindow(); base.Dispose(); }

        private static void OpenWindow()
        {
            var map = GameMain.GameSession?.Campaign?.Map;
            if (map == null || map.Locations == null || map.Locations.Count == 0)
            {
                GUI.AddMessage("BiomeJump: кампания не загружена (нет Map.Locations)", new Color(255, 100, 100));
                return;
            }

            _window = new GUIMessageBox(
                "🗺 BIOME JUMP", "",
                Array.Empty<LocalizedString>(), new Vector2(0.7f, 0.85f));
            var content = _window.Content;
            content.ClearChildren();

            var statusFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.09f), content.RectTransform, Anchor.TopCenter), style: null);
            statusFrame.Color = new Color(40, 30, 55);
            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.92f), statusFrame.RectTransform, Anchor.Center),
                "", textAlignment: Alignment.CenterLeft)
            { Wrap = true };
            _status.TextColor = new Color(220, 170, 255);

            var listFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.68f), content.RectTransform, Anchor.Center), style: null);
            _locList = new GUIListBox(new RectTransform(Vector2.One, listFrame.RectTransform));
            _locList.Color = new Color(18, 22, 32);

            var btnRow = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 0.12f), content.RectTransform, Anchor.BottomCenter),
                isHorizontal: true)
            { RelativeSpacing = 0.02f };

            var jumpBtn = new GUIButton(
                new RectTransform(new Vector2(0.42f, 1f), btnRow.RectTransform),
                "🗺 ПРЫГНУТЬ (SelectLocation)");
            jumpBtn.Color = new Color(150, 60, 170);
            jumpBtn.ToolTip = "Отправить selectedLocIndex — кампания сервера целимся в выбранную локацию.\n" +
                              "Следующий TryStartGame запустит раунд там.";
            jumpBtn.OnClicked = (b, d) => { Jump(); return true; };

            var tpBtn = new GUIButton(
                new RectTransform(new Vector2(0.28f, 1f), btnRow.RectTransform),
                "SetLocation (нужен флаг)");
            tpBtn.Color = new Color(90, 90, 140);
            tpBtn.ToolTip = "Телепорт ТЕКУЩЕЙ точки карты (не цели). Требует AllowDebugTeleport=true " +
                            "на сервере (обычно выключен).";
            tpBtn.OnClicked = (b, d) => { SetLoc(); return true; };

            var closeBtn = new GUIButton(
                new RectTransform(new Vector2(0.24f, 1f), btnRow.RectTransform), "Закрыть");
            closeBtn.Color = new Color(90, 100, 120);
            closeBtn.OnClicked = (b, d) => { CloseWindow(); return true; };

            FillLocations();
            UpdateStatus();
        }

        private static void FillLocations()
        {
            var map = GameMain.GameSession?.Campaign?.Map;
            if (map?.Locations == null || _locList == null) { return; }

            for (int i = 0; i < map.Locations.Count; i++)
            {
                int idx = i;
                var loc = map.Locations[i];
                string name;
                try { name = loc.DisplayName?.Value ?? loc.ToString(); } catch { name = "?"; }

                bool isCurrent = false;
                try { isCurrent = map.CurrentLocation == loc; } catch { }
                bool isRadiated = false;
                try { isRadiated = loc.IsRadiated(); } catch { }
                bool isCrit = false;
                try { isCrit = loc.IsCriticallyRadiated(); } catch { }

                string tag = "";
                if (isCurrent) { tag += "[ТЕКУЩАЯ] "; }
                if (isCrit) { tag += "[☢☢] "; }
                else if (isRadiated) { tag += "[☢] "; }

                var row = new GUIFrame(
                    new RectTransform(new Vector2(1f, 0.06f), _locList.Content.RectTransform), style: null);
                row.Color = new Color(32, 40, 55);
                row.UserData = idx;

                var btn = new GUIButton(
                    new RectTransform(Vector2.One, row.RectTransform),
                    $"[{idx}] {tag}{name}", textAlignment: Alignment.CenterLeft);
                btn.Color = new Color(34, 42, 58);
                btn.HoverColor = new Color(80, 60, 110);
                btn.OnClicked = (b, d) =>
                {
                    _selectedIdx = idx;
                    _selectedName = name;
                    UpdateStatus();
                    // Подсветить выбранную строку — пересобрать цвета
                    foreach (GUIComponent child in _locList.Content.Children)
                    {
                        if (child.UserData is int childIdx)
                        {
                            child.Color = childIdx == idx ? new Color(80, 40, 100) : new Color(32, 40, 55);
                        }
                    }
                    return true;
                };
            }
        }

        private static void UpdateStatus()
        {
            if (_status == null) { return; }
            var map = GameMain.GameSession?.Campaign?.Map;
            int count = map?.Locations?.Count ?? 0;
            bool isOwner = false;
            try { isOwner = GameMain.Client?.IsOwner ?? false; } catch { }

            // перм-держатели
            var holders = new List<string>();
            try
            {
                foreach (var c in GameMain.Client?.ConnectedClients ?? Enumerable.Empty<Client>())
                {
                    bool holder = c.IsOwner || c.HasPermission(ClientPermissions.ManageCampaign);
                    if (!holder) { continue; }
                    string st;
                    if (!c.InGame) { st = "лобби"; }
                    else if (c.Character == null) { st = "нет чара"; }
                    else if (c.Character.IsDead) { st = "труп"; }
                    else if (c.Character.IsIncapacitated) { st = "нок"; }
                    else { st = "ЖИВ"; }
                    holders.Add($"{c.Name}[{st}]");
                }
            }
            catch { }

            bool anyAlive = holders.Any(h => h.Contains("ЖИВ"));
            bool fallback = !anyAlive || count == 0;

            _status.Text =
                $"Локаций на карте: {count}\n" +
                $"Цель: {(_selectedIdx >= 0 ? $"[{_selectedIdx}] {_selectedName}" : "не выбрана")}\n" +
                $"Держатели прав: {(holders.Count > 0 ? string.Join(", ", holders) : "нет онлайн")}\n" +
                $"Фоллбэк ManageMap: {(fallback ? "✅ ОТКРЫТ" : "❌ ЗАКРЫТ (есть живой держатель)")}" +
                (isOwner ? " | ты Owner" : "");
        }

        private static void Jump()
        {
            if (_selectedIdx < 0)
            {
                GUI.AddMessage("BiomeJump: выбери локацию из списка", new Color(255, 100, 100));
                return;
            }
            SendCampaignPacket(_selectedIdx, null);
        }

        private static void SetLoc()
        {
            if (_selectedIdx < 0)
            {
                GUI.AddMessage("BiomeJump: выбери локацию из списка", new Color(255, 100, 100));
                return;
            }
            SendCampaignPacket(null, _selectedIdx);
        }

        /// <summary>
        /// Пакет MultiPlayerCampaign.ServerRead:
        /// u16 curIdx, u16 selIdx, byte missionCount, bools×3, crates, sold, upgrades, swaps
        /// curIdx null = пропустить SetLocation (нет AllowDebugTeleport)
        /// </summary>
        private static void SendCampaignPacket(int? setLocationIdx, int? selectLocationIdx)
        {
            try
            {
                var map = GameMain.GameSession?.Campaign?.Map;
                if (map == null) { return; }

                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
                msg.WriteUInt16((ushort)ClientPermissions.ManageCampaign);

                int curIdx = -1;
                try { curIdx = map.CurrentLocationIndex; } catch { }
                int sendCur = setLocationIdx ?? curIdx;
                msg.WriteUInt16(sendCur < 0 ? ushort.MaxValue : (ushort)sendCur);

                int sendSel = selectLocationIdx ?? curIdx;
                msg.WriteUInt16(sendSel < 0 ? ushort.MaxValue : (ushort)sendSel);

                // миссии — не трогаем
                msg.WriteByte(0);

                // ремонты/шаттл — false
                msg.WriteBoolean(false);
                msg.WriteBoolean(false);
                msg.WriteBoolean(false);

                // ящики и продажи — пусто
                msg.WriteByte(0); // buyCrate
                msg.WriteByte(0); // subSellCrate
                msg.WriteByte(0); // purchased
                msg.WriteByte(0); // sold

                msg.WriteUInt16(0); // upgrades
                msg.WriteUInt16(0); // swaps

                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);

                string action = setLocationIdx.HasValue
                    ? $"SetLocation({setLocationIdx})"
                    : $"SelectLocation({selectLocationIdx})";
                DebugConsole.NewMessage($"[BiomeJump] {action} отправлено", Color.Magenta);
                GUI.AddMessage($"BiomeJump: {action} — пакет ушёл. Следующий TryStartGame " +
                               $"запустит раунд в выбранной локации.", Color.Magenta);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[BiomeJump] Send: " + e.Message);
                GUI.AddMessage("BiomeJump: " + e.Message, Color.Red);
            }
        }

        private static void CloseWindow()
        {
            _window?.Close();
            _window = null;
            _locList = null;
            _status = null;
            _selectedIdx = -1;
        }
    }
}
