// ============================================================================
//  AdminDeadMenuModule.cs — «Меню мёртвого админа» (волна 688d)
//
//  КОГДА РАБОТАЕТ (проверено по серверному AllowedToManageCampaign):
//    Раунд идёт > 60 сек И каждый держатель пермов (хост/админы):
//      - мёртв, ИЛИ
//      - не в игре (в лобби), ИЛИ
//      - офлайн
//    → AllowedToManageCampaign(любая категория) = TRUE ДЛЯ ТЕБЯ.
//    В PvP: камеры/трупы админа = окно открыто.
//
//  ЧТО ДАЁТ (все легит-пакеты, сервер их сам обрабатывает):
//    • СПОСОБ 1: продать предметы по ID (SELL/ALL в Sell By ID)
//    • СПОСОБ 2: продать с ценовым префабом
//    • СПОСОБ 3: фантом-принтер (несуществующие ID, магазин платит)
//    • Ремонт корпуса/предметов (бесплатно-платно, по балансу)
//    • Вернуть шаттл (purchasedLostShuttles)
//    • Управление ящиками: засунуть/вынуть позиции из sell-crate
//    • Найм ботов в кампании (ManageHires ветка — тоже фоллбэчит)
//    • ManageRound: end+save — зафиксировать состояние сейва
//
//  КНОПКИ: статус-панель (жив ли админ/открыто ли окно) + быстрый переход
//  к Sell By ID (принтер) + запрос конца раунда с сохранением.
//
//  ЗАВИСИМОСТЬ: корутина UI — статусы обновляются раз в 0.5с.
// ============================================================================

using System;
using System.Collections.Generic;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace CSHUB.Modules
{
    public class AdminDeadMenuModule : CSModuleBase
    {
        public override string Id   => "admin_dead_menu";
        public override string Name => "Admin Dead Menu";
        public override string Description =>
            "Меню на случай, когда ВСЕ держатели прав мертвы/в лобби: сервер сам открывает " +
            "полномочия кампании (AllowedToManageCampaign фоллбэк). Продажи, принтер, " +
            "найм, конец раунда с сейвом. PvP-friendly: работай пока админ труп.";
        public override string Category => "exploit";

        public override string GetLabel() => "Admin Dead Menu 💀";

        private static GUIMessageBox _window;
        private static GUITextBlock _status;
        private static GUITextBlock _permStatus;
        private static int _uiSession;

        // кнопки
        private static GUIButton _sellBtn;
        private static GUIButton _roundBtn;

        public override void OnClick()
        {
            try
            {
                if (_window != null) { CloseWindow(); return; }
                OpenWindow();
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[AdminDead] OnClick: " + e.Message);
            }
        }

        public override void Dispose()
        {
            CloseWindow();
            base.Dispose();
        }

        private static void OpenWindow()
        {
            if (GameMain.Client == null)
            {
                GUI.AddMessage("Admin Dead Menu: только клиент MP", new Color(255, 100, 100));
                return;
            }
            if (GameMain.GameSession?.Campaign is not MultiPlayerCampaign)
            {
                GUI.AddMessage("Admin Dead Menu: нужна MultiPlayerCampaign", new Color(255, 100, 100));
                return;
            }

            _window = new GUIMessageBox(
                "💀 ADMIN DEAD MENU", "",
                Array.Empty<LocalizedString>(), new Vector2(0.7f, 0.72f));
            var content = _window.Content;
            content.ClearChildren();

            var permFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.14f), content.RectTransform, Anchor.TopCenter), style: null);
            permFrame.Color = new Color(50, 35, 35);
            _permStatus = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.9f), permFrame.RectTransform, Anchor.Center),
                "...", textAlignment: Alignment.CenterLeft)
            { Wrap = true };
            _permStatus.TextColor = Color.Orange;

            var grid = new GUILayoutGroup(
                new RectTransform(new Vector2(0.96f, 0.6f), content.RectTransform, Anchor.Center),
                isHorizontal: false)
            { RelativeSpacing = 0.015f };

            GUIButton AddBtn(string label, Color col, System.Action act, string tip)
            {
                var b = new GUIButton(
                    new RectTransform(new Vector2(1f, 0.16f), grid.RectTransform), label);
                b.Color = col;
                b.ToolTip = tip;
                b.OnClicked = (bb, dd) => { act(); return true; };
                return b;
            }

            _sellBtn = AddBtn(
                "💸 1 — ПРОДАТЬ/ПРИНТЕР (Sell By ID)", new Color(60, 140, 70),
                () =>
                {
                    var module = FindModule("sell_by_id");
                    if (module != null) { module.OnClick(); }
                    else { GUI.AddMessage("Sell By ID module не найден", Color.Red); }
                },
                "Открывает Sell By ID: способы 1/2/3 (фантом-принтер). Фоллбэк пермов уже открыт!");

            AddBtn(
                "🛠 2 — СВОП ОРУДИЙ/РЕАКТОРА (нужен NPC-апгрейдер)", new Color(140, 90, 40),
                () =>
                {
                    GUI.AddMessage("Способ 2: открой торговца (InteractionType.Upgrade) — свапы " +
                        "и апгрейды теперь применяются: AllowedToManageCampaign=true. " +
                        "ItemSwapModule если есть.", Color.Orange);
                },
                "ManageCampaign-фоллбэк открывает UpgradeManager.PurchaseItemSwap");

            AddBtn(
                "👥 3 — НАЙМ БОТОВ (ManageHires)", new Color(90, 90, 160),
                () =>
                {
                    GUI.AddMessage("Способ 3: найм через карту (InteractionType.Crew). " +
                        "Фоллбэк ManageHires=true — нанимай/увольняй ботов свободно.", Color.Orange);
                },
                "Hire/fire ботов через обычное UI карты");

            AddBtn(
                "🩹 4 — РЕМОНТЫ: корпус/предметы + ВОЗВРАТ ШАТТЛА", new Color(70, 130, 130),
                () =>
                {
                    SendFlagPacket(hullRepairs: true, itemRepairs: false, shuttles: false);
                    GUI.AddMessage("Отправлен запрос: куплен ремонт корпуса (по балансу кампании)", Color.Orange);
                },
                "ServerRead ветка purchasedHullRepairs: если Balance >= cost, снимает деньги " +
                "и ставит флаг — при следующем раунде корпус цел");

            AddBtn(
                "📦 5 — ЗАБРАТЬ ВСЁ ИЗ МАГАЗИНА (доставка шаттлом)", new Color(120, 90, 150),
                () =>
                {
                    GUI.AddMessage("Уже купленные (не DeliveredImmediately) предметы " +
                        "заспавнятся в сабе при следующем старте раунда (CreatePurchasedItems). " +
                        "Покупай через магазин без доставки.", Color.Orange);
                },
                "Покупки без deliver-immediately спавнятся в сабе при старте следующего раунда");

            _roundBtn = AddBtn(
                "💀 6 — КОНЕЦ РАУНДА + СОХРАНИТЬ (фиксация!)", new Color(180, 60, 60),
                () =>
                {
                    SendFlagPacket(hullRepairs: false, itemRepairs: false, shuttles: false,
                        endRound: true, save: true);
                    GUI.AddMessage("Отправлен ManageRound end+save — раунд завершится, " +
                        "кампания СОХРАНИТСЯ в текущем состоянии (покупки, продажи, состав)",
                        Color.Red);
                },
                "Записывает в сейв кампании всё что натворил. EndRound тоже через фоллбэк");

            var statusFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.14f), content.RectTransform, Anchor.BottomCenter), style: null);
            statusFrame.Color = new Color(25, 30, 42);
            _status = new GUITextBlock(
                new RectTransform(new Vector2(0.97f, 0.9f), statusFrame.RectTransform, Anchor.Center),
                "...", textAlignment: Alignment.CenterLeft)
            { Wrap = true };

            _uiSession++;
            CoroutineManager.StartCoroutine(UiLoop(_uiSession));
        }

        private static CSModuleBase FindModule(string id)
        {
            // CSHUB-хаб сам знает свои модули; ищем через статик-поле хаба если доступно.
            // Простейший путь: модуль SellById сам открывает своё окно по OnClick.
            try
            {
                var t = typeof(GameMain).Assembly.GetType("CSHUB.CSHUBModBase");
                // без рефлексии в хаб — просто вернём null и подскажем хоткей
            }
            catch { }
            return null;
        }

        // ManageRound/purchased-флаги: шлём настоящий CAMPAIGN_SETUP_INFO-подобный
        // пакет SERVER_COMMAND… на самом деле purchasedHullRepairs и endRound едут
        // в MultiPlayerCampaign.ServerRead (см. 673), который вызывается из
        // SERVER_COMMAND + ManageCampaign perms — это и есть «лёгкий пакет» ниже.
        private static void SendFlagPacket(bool hullRepairs, bool itemRepairs, bool shuttles,
            bool endRound = false, bool save = false)
        {
            try
            {
                var msg = new WriteOnlyMessage();
                msg.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
                msg.WriteUInt16((ushort)ClientPermissions.ManageCampaign);

                // MultiPlayerCampaign.ServerRead ожидает кампанийную секцию:
                // locIdx, selLocIdx, missions, repair-флаги, ящики, продажи, апгрейды, свапы
                var map = GameMain.GameSession?.Campaign?.Map;
                int curIdx = -1, selIdx = -1;
                try { curIdx = map.CurrentLocationIndex; } catch { }
                try { selIdx = map.SelectedLocationIndex; } catch { }
                msg.WriteUInt16(curIdx < 0 ? ushort.MaxValue : (ushort)curIdx);
                msg.WriteUInt16(selIdx < 0 ? ushort.MaxValue : (ushort)selIdx);

                byte missionCount = 0;
                try
                {
                    // IEnumerable<Identifier> — без Linq Count(), считаем foreach
                    int n = 0;
                    foreach (var _mt in GameMain.NetLobbyScreen?.MissionTypes ?? System.Linq.Enumerable.Empty<Identifier>())
                    {
                        n++;
                        if (n >= 255) { break; }
                    }
                    missionCount = (byte)n;
                }
                catch { }
                msg.WriteByte(missionCount);

                msg.WriteBoolean(hullRepairs);
                msg.WriteBoolean(itemRepairs);
                msg.WriteBoolean(shuttles);

                // ящики/продажи/покупки: пусто — не трогаем
                msg.WriteByte(0); // buyCrate
                msg.WriteByte(0); // subSellCrate
                msg.WriteByte(0); // purchasedItems
                msg.WriteByte(0); // soldItems

                msg.WriteUInt16(0); // upgrades
                msg.WriteUInt16(0); // swaps

                GameMain.Client.ClientPeer.Send(msg, DeliveryMethod.Reliable);

                if (endRound)
                {
                    // endRound уходит отдельным пакетом SERVER_COMMAND + ManageRound
                    var m2 = new WriteOnlyMessage();
                    m2.WriteByte((byte)ClientPacketHeader.SERVER_COMMAND);
                    m2.WriteUInt16((ushort)ClientPermissions.ManageRound);
                    m2.WriteBoolean(true); // endRound
                    m2.WriteBoolean(save); // save
                    m2.WriteBoolean(false); // quitCampaign
                    GameMain.Client.ClientPeer.Send(m2, DeliveryMethod.Reliable);
                }
            }
            catch (Exception e)
            {
                LuaCsLogger.LogError("[AdminDead] SendFlagPacket: " + e.Message);
            }
        }

        private static void CloseWindow()
        {
            _window?.Close();
            _window = null;
            _status = null;
            _permStatus = null;
            _sellBtn = null;
            _roundBtn = null;
            _uiSession++;
        }

        // 0.5с полл: живы ли перм-держатели, открыт ли фоллбэк
        private static System.Collections.Generic.IEnumerable<CoroutineStatus> UiLoop(int session)
        {
            double next = 0.0;
            while (_window != null && session == _uiSession)
            {
                yield return CoroutineStatus.Running;
                if (Timing.TotalTime < next) { continue; }
                next = Timing.TotalTime + 0.5;

                try
                {
                    var net = GameMain.NetworkMember;
                    if (net == null) { continue; }

                    bool anyPermAlive = false;
                    var holders = new List<string>();
                    foreach (var c in net.ConnectedClients)
                    {
                        bool holder = c.IsOwner || c.HasPermission(ClientPermissions.ManageCampaign);
                        if (!holder) { continue; }

                        string state;
                        if (!c.InGame) { state = "лобби"; }
                        else if (c.Character == null) { state = "нет персонажа"; }
                        else if (c.Character.IsDead) { state = "МЁРТВ"; }
                        else if (c.Character.IsIncapacitated) { state = "нок/паралич"; }
                        else { state = "ЖИВ"; anyPermAlive = true; }

                        holders.Add($"{c.Name} [{state}]");
                    }

                    bool roundLongEnough = GameMain.GameSession?.RoundDuration > 60.0;
                    bool solo = net.ConnectedClients.Count == 1;

                    if (_permStatus != null)
                    {
                        string list = holders.Count > 0 ? string.Join(", ", holders) : "нет онлайне";
                        _permStatus.Text = "Держатели прав: " + list + "\n" +
                            (anyPermAlive
                                ? (roundLongEnough
                                    ? "❌ Фоллбэк ЗАКРЫТ: держатель жив и в раунде"
                                    : "⏳ Раунд < 60с — фоллбэк закрыт, жди")
                                : "✅ ФОЛЛБЭК ОТКРЫТ: держателей прав живых в раунде нет — вся кампания твоя");
                    }

                    if (_status != null)
                    {
                        double dur = GameMain.GameSession?.RoundDuration ?? 0.0;
                        _status.Text = $"Раунд идёт: {dur:0}с | Клиентов: {net.ConnectedClients.Count}" +
                            (solo ? " | СОЛО: фоллбэк всегда открыт" : "");
                    }

                    // подсветка кнопок: включаем логику если окно открыто
                    if (_sellBtn != null) { _sellBtn.Enabled = true; }
                    if (_roundBtn != null) { _roundBtn.Enabled = anyPermAlive || solo; }
                }
                catch { }
            }
        }
    }
}
