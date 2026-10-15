// ============================================================================
//  PhantomCampaignModule.cs — «ФАНТОМ-КАМПАНИЯ» (волна 699)
//
//  Заменяет АКТИВНУЮ кампанию сервера новой со своими множителями.
//  Требует окно ManageRound-фоллбэка (админ мёртв / в лобби / раунд > 60с).
//
//  ЧТО ПРОИСХОДИТ НА СЕРВЕРЕ (MultiPlayerCampaign.cs:121-135):
//    localSavePath = SaveUtil.CreateSavePath(Multiplayer, saveName)
//      → старый сейв НЕ удаляется, создаётся новый файл
//    GameMain.GameSession = new GameSession(new SubmarineInfo(subPath), ...)
//      → активная кампания ПЕРЕЗАПИСЫВАЕТСЯ в памяти сервера
//    SaveUtil.SaveGame(DataPath) → новый сейв записан на диск
//    LoadInitialLevel() → стартует раунд новой кампании
//
//  РЕЗУЛЬТАТ: прогресс старой кампании стирается ДЛЯ СЕРВЕРА. Все игроки
//  (включая воскресшего капитана) попадают в ТВОЮ кампанию с ТВОИМИ
//  множителями. Восстановить старую можно только вручную с диска хоста.
//
//  ФОРМАТ ПАКЕТА (GameServer.cs:889-925):
//    byte  CAMPAIGN_SETUP_INFO
//    bool  isNew = true
//    padBits
//    string saveName
//    string seed
//    string subName
//    string subHash (MD5 — берём из GetSubList по имени)
//    CampaignSettings (INetSerializableStruct):
//      string PresetName
//      bool RadiationEnabled          + padBits (после bool)
//      int  MaxMissionCount (ranged 1-10)
//      byte WorldHostility (enum ranged)
//      Identifier StartItemSet
//      byte StartingBalanceAmount (enum ranged)
//      float CrewVitalityMultiplier
//      float NonCrewVitalityMultiplier
//      float OxygenMultiplier
//      float FuelMultiplier
//      float MissionRewardMultiplier
//      float ExperienceRewardMultiplier
//      float ShopPriceMultiplier
//      float ShipyardPriceMultiplier
//      float RepairFailMultiplier
//      float DeteriorationMultiplier
//      bool  HeartsGangRemovable      + padBits
//      byte  PatdownProbability
//      ...
//      (NetworkSerialize поля в порядке объявления класса)
//
//  ВНИМАНИЕ: заменяет кампанию БЕЗВОЗВРАТНО (для сервера). Не жми на
//  серверах, которые тебе дороги. Проверено: старый сейв остаётся на диске,
//  но сервер про него забывает (DataPath теперь указывает на новый).
// ============================================================================

using System;
using System.Collections.Immutable;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class PhantomCampaignModule : CSModuleBase
    {
        public override string Id   => "phantom_campaign";
        public override string Name => "Phantom Campaign";
        public override string Description =>
            "Замена активной кампании сервера новой со СВОИМИ множителями: награда x2, цены x0.5, " +
            "HP x2, кислород x1.2, топливо x0.5, сложность low. Прогресс старой стирается для сервера " +
            "(файл сейва остаётся, но сервер его забывает). Требует ManageRound фоллбэк " +
            "(админ мёртв/лобби/раунд > 60с).";
        public override string Category => "exploit";

        public override string GetLabel() => "Phantom Campaign 👻";

        private static GUIMessageBox _window;
        private static GUITextBox _saveNameBox, _seedBox, _subNameBox;
        private static GUITextBlock _status;

        // множители (можно крутить цифры прямо в коде)
        private static float CrewHp      = 2.0f;
        private static float RewardMul   = 2.0f;
        private static float ExpMul      = 2.0f;
        private static float ShopMul     = 0.5f;
        private static float OxyMul      = 1.2f;
        private static float FuelMul     = 0.5f;

        public override void OnClick()
        {
            try
            {
                if (_window != null) { _window.Close(); _window = null; return; }
                OpenWindow();
            }
            catch (Exception e) { LuaCsLogger.LogError("[PhantomCampaign] " + e.Message); }
        }

        private static void OpenWindow()
        {
            var net = GameMain.NetworkMember;
            if (net == null)
            {
                GUI.AddMessage("PhantomCampaign: только MP", new Color(255, 100, 100));
                return;
            }

            // авто: подставить текущую выбранную субу из лобби
            string defaultSub = "";
            try
            {
                var subs = GameMain.NetLobbyScreen?.GetSubList();
                var sel = GameMain.NetLobbyScreen?.SelectedSub;
                if (sel != null) { defaultSub = sel.Name; }
                else if (subs != null && subs.Count > 0) { defaultSub = subs[0].Name; }
            }
            catch { }

            _window = new GUIMessageBox(
                "👻 PHANTOM CAMPAIGN",
                "", Array.Empty<LocalizedString>(), new Vector2(0.7f, 0.75f));
            var content = _window.Content;
            content.ClearChildren();

            var grid = new GUILayoutGroup(
                new RectTransform(new Vector2(0.96f, 0.65f), content.RectTransform, Anchor.TopCenter))
            { RelativeSpacing = 0.01f };

            void AddRow(string label, out GUITextBox box, string defVal)
            {
                var r = new GUILayoutGroup(
                    new RectTransform(new Vector2(1f, 0.16f), grid.RectTransform), isHorizontal: true)
                { RelativeSpacing = 0.01f };
                new GUITextBlock(
                    new RectTransform(new Vector2(0.35f, 1f), r.RectTransform), label,
                    textAlignment: Alignment.CenterLeft)
                { CanBeFocused = false };
                var fr = new GUIFrame(new RectTransform(new Vector2(0.62f, 1f), r.RectTransform), style: null);
                box = new GUITextBox(new RectTransform(Vector2.One, fr.RectTransform), defVal);
            }

            AddRow("Имя сейва:", out _saveNameBox, "phantom_campaign");
            AddRow("Seed:", out _seedBox, "phantom");
            AddRow("Имя субы:", out _subNameBox, defaultSub);

            var infoFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.2f), content.RectTransform, Anchor.Center), style: null);
            infoFrame.Color = new Color(50, 35, 55);
            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.95f), infoFrame.RectTransform, Anchor.Center),
                $"Множители (захардкожены, правь константы):\n" +
                $"HP экипажа: x{CrewHp} | Награда: x{RewardMul} | Опыт: x{ExpMul}\n" +
                $"Цены магазинов: x{ShopMul} | Кислород: x{OxyMul} | Топливо: x{FuelMul}\n" +
                $"Сложность: Low | Враждебность: Low | Старт: High (10k mk)\n" +
                $"Радиация: OFF | Патдауны: off",
                textAlignment: Alignment.CenterLeft)
            { Wrap = true };
            _status.TextColor = new Color(220, 170, 255);

            var launchBtn = new GUIButton(
                new RectTransform(new Vector2(0.8f, 0.09f), content.RectTransform, Anchor.BottomCenter),
                "👻 ЗАПУСТИТЬ ФАНТОМ-КАМПАНИЮ");
            launchBtn.Color = new Color(150, 60, 170);
            launchBtn.OnClicked = (b, d) =>
            {
                LaunchPhantom(_saveNameBox.Text?.Trim() ?? "phantom_campaign",
                              _seedBox.Text?.Trim() ?? "phantom",
                              _subNameBox.Text?.Trim() ?? defaultSub);
                return true;
            };

            var closeBtn = new GUIButton(
                new RectTransform(new Vector2(0.2f, 0.07f), content.RectTransform, Anchor.BottomRight),
                "Закрыть");
            closeBtn.Color = new Color(90, 100, 120);
            closeBtn.OnClicked = (b, d) => { _window?.Close(); _window = null; return true; };
        }

        private static void LaunchPhantom(string saveName, string seed, string subName)
        {
            try
            {
                // найти выбранную субу в списке лобби и взять её MD5 hash
                string subHash = "";
                try
                {
                    var subs = GameMain.NetLobbyScreen?.GetSubList();
                    var match = subs?.FirstOrDefault(s => s.Name == subName);
                    if (match != null) { subHash = match.MD5Hash.StringRepresentation; }
                }
                catch { }

                if (string.IsNullOrEmpty(subHash))
                {
                    GUI.AddMessage($"PhantomCampaign: суба \"{subName}\" не найдена в списке сервера. " +
                        "Возьми имя из списка серверных суб (открыть меню выбора субы).",
                        new Color(255, 100, 100));
                    return;
                }

                // Создаём CampaignSettings через рефлексию — он internal.
                // Все NetworkSerialize поля надо проставить чтобы структура
                // прошла через INetSerializableStruct.Write правильно.
                var asm = typeof(GameMain).Assembly;
                var tSettings = asm.GetType("Barotrauma.CampaignSettings");
                if (tSettings == null)
                {
                    GUI.AddMessage("PhantomCampaign: CampaignSettings не найден", Color.Red);
                    return;
                }

                var settings = Activator.CreateInstance(tSettings, new object[] { null });

                void Set(string propName, object value)
                {
                    try
                    {
                        var pi = tSettings.GetProperty(propName,
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        pi?.SetValue(settings, value);
                    }
                    catch (Exception ex)
                    {
                        LuaCsLogger.LogError($"[PhantomCampaign] Set {propName}: {ex.Message}");
                    }
                }

                Set(nameof(PhantomSettings.PresetName), "Phantom");
                Set(nameof(PhantomSettings.TutorialEnabled), false);
                Set(nameof(PhantomSettings.RadiationEnabled), false);
                Set(nameof(PhantomSettings.MaxMissionCount), 3);
                Set(nameof(PhantomSettings.WorldHostility), WorldHostilityOption.Low);
                Set(nameof(PhantomSettings.StartItemSet), "easy".ToIdentifier());
                Set(nameof(PhantomSettings.StartingBalanceAmount), StartingBalanceAmountOption.High);
                Set(nameof(PhantomSettings.CrewVitalityMultiplier), CrewHp);
                Set(nameof(PhantomSettings.NonCrewVitalityMultiplier), 1.0f);
                Set(nameof(PhantomSettings.OxygenMultiplier), OxyMul);
                Set(nameof(PhantomSettings.FuelMultiplier), FuelMul);
                Set(nameof(PhantomSettings.MissionRewardMultiplier), RewardMul);
                Set(nameof(PhantomSettings.ExperienceRewardMultiplier), ExpMul);
                Set(nameof(PhantomSettings.ShopPriceMultiplier), ShopMul);
                Set(nameof(PhantomSettings.ShipyardPriceMultiplier), ShopMul);
                Set(nameof(PhantomSettings.RepairFailMultiplier), 0.0f);
                Set(nameof(PhantomSettings.DeteriorationMultiplier), 0.0f);
                Set(nameof(PhantomSettings.PatdownProbability), PatdownProbabilityOption.Off);

                // пакуем
                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.CAMPAIGN_SETUP_INFO);
                msg.WriteBoolean(true);  // isNew
                msg.WritePadBits();
                msg.WriteString(saveName);
                msg.WriteString(seed);
                msg.WriteString(subName);
                msg.WriteString(subHash);

                var structWriteMethod = typeof(INetSerializableStruct)
                    .GetMethod("Write", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                structWriteMethod.Invoke(settings, new object[] { msg });

                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);

                DebugConsole.NewMessage($"[PhantomCampaign] пакет отправлен: saveName={saveName}, " +
                    $"sub={subName}, seed={seed}", Color.Magenta);
                GUI.AddMessage("PhantomCampaign: пакет отправлен. Раунд запустится автоматически " +
                    "(LoadInitialLevel). Прогресс старой кампании стёрт для сервера.",
                    Color.Magenta);
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[PhantomCampaign] Launch: " + e.Message);
                GUI.AddMessage("PhantomCampaign: " + e.Message, Color.Red);
            }
        }

        // Указатель полей CampaignSettings для рефлексии (компайл-тайм не доступен — internal)
        private static class PhantomSettings
        {
            public const string PresetName = "PresetName";
            public const string TutorialEnabled = "TutorialEnabled";
            public const string RadiationEnabled = "RadiationEnabled";
            public const string MaxMissionCount = "MaxMissionCount";
            public const string WorldHostility = "WorldHostility";
            public const string StartItemSet = "StartItemSet";
            public const string StartingBalanceAmount = "StartingBalanceAmount";
            public const string CrewVitalityMultiplier = "CrewVitalityMultiplier";
            public const string NonCrewVitalityMultiplier = "NonCrewVitalityMultiplier";
            public const string OxygenMultiplier = "OxygenMultiplier";
            public const string FuelMultiplier = "FuelMultiplier";
            public const string MissionRewardMultiplier = "MissionRewardMultiplier";
            public const string ExperienceRewardMultiplier = "ExperienceRewardMultiplier";
            public const string ShopPriceMultiplier = "ShopPriceMultiplier";
            public const string ShipyardPriceMultiplier = "ShipyardPriceMultiplier";
            public const string RepairFailMultiplier = "RepairFailMultiplier";
            public const string DeteriorationMultiplier = "DeteriorationMultiplier";
            public const string PatdownProbability = "PatdownProbability";
        }
    }
}
