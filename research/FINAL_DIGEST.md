# BAROTRAUMA TRUST BOUNDARY AUDIT — ФИНАЛЬНЫЙ ДАЙДЖЕСТ
### 462 волны, 2518 файлов, 689,676 строк — все доверительные границы клиент→сервер

Дата: волна 462. Все форматы пакетов сверены с исходниками байт-в-байт.
Модули: PacketDumpModule, SellByIdModule, TalentOverspend(×2), FreeHealModule.

---

## ★★★★★ КРИТ — эксплуатируемо в лайве, экономика/приватность

### 1. Продажа несуществующих предметов [волна 452]
**Корень**: `CargoManager.SellItems` — оплата НЕ зависит от владения.
- `Origin=Character, itemId=0` → сервер не ищет предмет (фейлсейф только для Submarine) → **оплата без удаления** = печать денег
- `itemId=<чужой предмет>` → сервер **удаляет из мира** и платит тебе (кража; ID видны клиенту)
- `BuyBackSoldItems`: `store.Balance += itemValue` **ДО** `TryPurchase` (фейл = без списания), Remove **после** → **вечный двигатель** баланса магазина
- Гейты: `AllowedToManageCampaign(SellInventoryItems)` — соло = всегда, «нет пермщиков» = все
**Пакет**: `u8 10 | u16 0x0040 | u16 loc | u16 loc | u8 0 | bool×3=0 | byte 0 ×3 | byte 1 | Identifier storeId | u16 N | N×{Identifier prefabId, u16 itemId=0, bool 0, byte 0, byte 0} | u16 0 | u16 0`

### 2. Free Medical Clinic [волна 453] — **МОДУЛЬ ГОТОВ** (FreeHealModule.cs)
**Корень**: `NetAffliction.Price` — **клиентское** поле (ushort, без NetworkSerialize-клампа). Серверный калькулятор `SetAffliction` на клиентском пути не вызывается; `IsHealable` минуется; `IsOutpostInCombat` вне аутпоста = false → лечение **где угодно**; цель = любой Team1.
**Пакеты**: `u8 17 (MEDICAL) | u8 4 (ADD_PENDING) | NetCrewMember{CharacterInfoID, Afflictions[{Identifier, Strength, VitalityDecrease=0, Price=0}]}` затем `u8 17 | u8 7 (HEAL_PENDING)`.
Эффект: полная очистка тела за 0mk, husk-cure, снятие баффов пати (гриф, карма слепа).

### 3. Holdable attach — unit mismatch 150x [волна 447]
`MaxAttachDistance = 120×0.95 = 114 display px`, кламп `171` применяется к **sim**-юнитам (1 sim = 100 display) → **17,100 px** свободы сквозь стены. `AttachToWall` снаружи цепляет к **чужой субе/аутпосту/скале уровня** (`item.Submarine = attachTarget.Submarine`), `OwnInventory` летит вместе. Planter = кража грядок с растениями.
**Пакет**: Holdable event: `float simX, float simY` (любые, клампын 171 sim).

### 4. Steering autopilot — произвольная точка [волна 447]
`posToMaintain = new Vector2(ReadSingle(), ReadSingle())` — raw f32×2, нет IsValid/clamp/distance → бот ведёт суб куда угодно (радиация, монстры).
**Пакет**: Steering event: `u16 targetVelocity..., float posX, float posY`.

### 5. Сигнальная шина = доверенная; wifi = вход без аутентификации [447/448/460]
`Turret position_in/trigger_in`: нет дистанции/отправителя → wifi на нужной частоте = управление турелью с другого конца карты. База `ItemComponent.ReceiveSignal`: `activate/use/trigger_in → item.Use()` для ЛЮБОГО компонента. `wifi set_range="NaN"` → Range=NaN → дистанционные фильтры всегда false → **радиус ∞**.

---

## ★★★★ ВЫСОКИЙ — софт-DoS / постоянный ущерб

### 6. VOIP 1-байтовый батч-дроп [451]
Concentus `OpusDecoder.Decode`: `ret<0 → throw` (catch пере-кидывает). Буфер сервера = 960 сэмплов (20ms). TOC-байт 40/60ms (1920/2880) → `OPUS_BUFFER_TOO_SMALL` → **гарантированный throw**. Сервер декодирует всё voice для амплитуды. Исключение ≠ NetStructReadException → peer catch-all → **дроп остатка батча тика**. Спам битых фреймов = потеря INPUT/EVENTS/CHAT всех клиентов.
**Пакет**: `u8 7 (VOICE) | byte queueId | u16 LatestBufferID+1 | bool | 8×(byte len≤255, bytes)` — достаточно 1 фрейма с TOC=0xEC+ (60ms cfg).

### 7. Отрицательный перевод через голосование [455]
`VoteType.TransferMoney startVote`: `amount = ReadInt32()` **без `<=0`** (в прямом переводе чек есть!). `TryDeduct(-X)`: `Balance >= -X` всегда true → отправителю **+X**, получателю **−X навсегда** (кампейн-сейв). `from/to` — byte sessionId → null = **БАНК**.
**Пакет**: `u8 0 (UPDATE_LOBBY... via Vote segment) | VoteType.TransferMoney | bool true | int amount<0 | byte fromSession | byte toSession` → нужна 1 yes-поддержка.

### 8. ReadPropertyChange без клампов [448]
`ChangeProperty` event: Identifier + значение. Гейт один — CanClientAccess. Float/Vector2 ветви **не проверяют Editable.MinValue/MaxValue** (они клиентские!) → NaN/1e30 в Delay, Frequency, Range, MotionSensor... String[]-ветвь (memory-bomb) мертва: единственное string[] свойство — plain [Editable], сервер ищет OfType<InGameEditable> (не матчит наследование).

### 9. CAMPAIGN_SETUP_INFO + dosProtection.Pause [457]
Клиентский `savePath` → `LoadCampaign` (полный XML-парс) внутри `Pause()` = **иммунитет к DoS-стрикам**. Пермлесс = любой. File-oracle по латентности. (`GetBackupPath` = конкатенация без валидации.)

### 10. Resync-тоггл [458]
`ENDROUND_SELF` (без гейтов, ResetSync стирает прогресс) + `UPDATE_INGAME midroundSyncingDone=false` → сервер **ре-сериализует весь uniqueEvents-бэклог** на каждый цикл. Растёт с длиной раунда. Кик только по SyncTimeout (после выжимания).

---

## ★★★ СРЕДНИЙ

11. **[449] SEND_BACKUP_INDICES**: клиентский путь → `Directory.GetFiles` + N × (открыть+декомпрессия+XML-парс) синхронно. Без перм. DoS-амплитфикация: 1 пакет = N парсов. Формат: `u8 26 | string path`.
12. **[449] EventManager**: `Options[selectedOption]` — сырой байт 0-254 как индекс → throw → батч-дроп. Гейт TargetClients есть.
13. **[451] Order**: `PrefabCollection[unknown Identifier]` → KeyNotFound → батч-дроп (#2 семейства). `orderPriority` сырой байт (макс 5, шлют 255).
14. **[447] Wire nodes**: node positions raw f32×2 — рисуются через стены/в другие субы.
15. **[450] SpectatePos NaN** (мёртвый спектатор): `Math.Min(distSqr, NaN)=NaN` → все персонажи навсегда активны (CPU). Живым закрыто геттером-щитом (Client.cs:115).
16. **[448] Pump set_speed NaN**: сеттер проверяет СТАРОЕ значение (`IsValid(flowPercentage)` не `value`) → яд застревает навсегда; `set_targetlevel` → NaN.
17. **[449] Engine/Reactor NaN**: `Clamp(NaN)=NaN` → энергосеть отравлена (персистентно); физика суба спасена случайным `Math.Abs(Force)>1.0f`; реактор: meltdown-чеки всегда false = «бессмертный».

## ★★ НИЗКИЙ

18. **[456] ManageRound** на пермлесс-сервере: любой клиент `EndGame()` без вот-а (fallback «нет пермщиков = все»).
19. **[448] Repairable QTESuccess** bool верится клиенту (мини-чит буста ремонта).
20. **[448] CircuitBox**: MoveComponent = CanClientAccess без Locked; MoveAmount сырой Vector2 (UI); AddWire бесплатный.
21. **[461] Inventory transport**: `SharedRead` byte start/end vs capacity-массив → IndexOutOfRange (per-event caught, #6); `Item.cs:200` null-cast NRE (#7); `receivedItemIds` никогда не чистится на дисконнекте (медленный leak на сотнях контейнеров).
22. **[459] Karma**: реталиация-окно 120с, урон по предметам без штрафа (луп «сломал-починил»), клон −50%, латентный NRE `Info?.Job.Prefab` (Job null), низкокармовые = бесплатные груши, herpes-квантование (сознательно).
23. **[455] pendingVotes** очередь без лимита; **[458] HandleClientError** Log на каждый пакет + GA-ключи с entityID (спам).

## ✅ ПРОВЕРЕНО ЧИСТЫМ (не векторы)
Инпут персонажа (сервер считает курсор сам: `AimRefPos + dir*500`, CanInteractWith на interact-ID), чат (кламп 200, Levenshtein-спам с киком, мёртвые → Dead), FILE_REQUEST (имя+MD5), UPDATE_CHARACTERINFO (рейт-лимит+палитра), Hull (пермы/краскопульт-математика), Inventory-apply (пре-чек CanClientAccess, наручники отдельно), Character events (self-гейты), ачивки/гены/swap-цены/найм/трансферы (сервер-авторитарны), Talents (кроме 447-overspend), консоль (whitelist команд), голоса (SetVote только серверный), Karma (сервер-выводная), Traitor (сервер-выбор), EntitySpawner (XML-only), IdCard (TeamID сервером), ConnectionPanel (Locked+скилл-чек), Reactor/Engine/Sonar/Wifi/Terminal/CustomInterface/Pump ServerEventRead (клампы+гейты), ReadyCheck, VOICE-маршрутизация (мут/дистанция).

## 🧬 КОРНЕВЫЕ ПАТТЕРНЫ
1. **«Клиент шлёт ЧТО, сервер считает СКОЛЬКО»** — нарушено 2 раза: продажа (452), клиника (453). Везде иначе соблюдено.
2. **«После CanClientAccess — полное доверие пакету»** — все ServerEventRead.
3. **throw → peer catch-all → батч-дроп** (7 семейств): Options[255], PrefabCollection[?], OpusDecoder TOC, Inventory byte-overread, null-cast, ManageCampaign-хэши — лаг-машины из одного корня.
4. **NaN-инъекция**: TryParse/ReadSingle без IsValid на приёме (Pump, Wifi, ReadPropertyChange, Engine, Reactor) — единственный чистый Turret.
5. **Unit mismatch display↔sim** (ratio 100:1) — Holdable attach.
6. **DoSProtection считает пакеты, не стоимость** — амплитфикация через «дорогие» пакеты + Pause-иммунитет (457).
7. **Фреймворк-клампы существуют** (`[NetworkSerialize(MinValueInt..)]`, NetIdUtils.Clamp) — применены точечно; забыли на NetAffliction.Price и ReadPropertyChange.

*Сформировано волной 462. Полные разборы: research/RESEARCH_NOTES.txt (волны 2-462), формат пакета каждого вектора — в соответствующем блоке.*

---
---

# PART 2 — ВОЛНЫ 463-727 (сессия продолжения)
### Мод-хост CSHUB (39 модулей), верификация бинарника 1.13.4.0, живые тесты

---

## ★★★★★ КРИТ — crash/OOM-класс (6-байтовые пакеты против гигабайт)

### E1. EventBomb [715-716] — **МОДУЛЬ: EventBombModule.cs, ПОДТВЕРЖДЁН ЖИВЫМ ТЕСТОМ**
Конверт энти-событий: `msgLength = ReadVariableUInt32()` (сырой varint) →
`msg.ReadBytes(msgLength - 2)` → `new byte[N]` **АЛЛОКАЦИЯ ДО ПРОВЕРОК**
(ServerEntityEventManager.Read:475, MsgReader.ReadBytes:427). BlockCopy кидает
ArgumentException ПОСЛЕ аллокации. **Перм-гейта НЕТ** (обычный путь каждого клика).
**0 киков** (≠NetStructReadException), **0 страйков** (memset 200MB ≈ 15мс < 100мс).
Exception ДО `LastSentEntityEventID++` → счётчик замирает → burst с одним ID.
**Формат**: UPDATE_INGAME(1) | bool true+pad | int32 ptr-ph | [pad | u16 firstEventID
(mirror+1..+3) | byte 1 | u16 entityID (труп/любой Item) | varuint32 len | u16 0] |
table{u16 1 + Segment<ClientNetSegment>{EntityState,ptr}} (Segment`1 public → рефлексия).
CHURN x20 = 4GB/с. Лобби дропает пакет (GameStarted-гейт!). Бонус: skip-путь
`BitPosition += msgLength*8` — int overflow при ≥2^29 → rewind (replay).

### E2. DecompBomb [714] — **МОДУЛЬ: DecompBombModule.cs**
`ReadOnlyMessage` ctor: `DeflateStream.CopyTo` **БЕЗ лимита выхода** (Message.cs:654).
Заголовок `PacketHeader.IsCompressed` ставим руками через рефлексию SendMsgInternal
(автосжиматель клиента порог 1000B не сработает). 60MB нулей → 59KB wire → пик ×2-3.
★★★ **Разжатие идёт ДО `dosProtection.Start` → время декомпрессии НЕ ТАЙМЕРИТСЯ:
SIEGE 66/с (весь минутный рейт) НЕ КИКАЕТ за замедление — пулемёт ~4GB/с.**
Контент после разжатия: 0x07 PING_RESPONSE (len 0≠64 → чистый return). Оба транспорта, из лобби.

### E3. PerkBomb [713] — **МОДУЛЬ: PerkBombModule.cs**
`ServerSettings.ReadPerks`: `new Identifier[ReadVariableUInt32()]` без клампа
(ReadArray-кламп этот путь обходит). Гейт `HasPermissionToChangePerks`:
открыт, если **ни у кого онлайн нет ManageSettings** (дедик без админов; PvP — своя команда).
Identifier ~4-8B → 200M count = 0.8-1.6GB зануления с 6-байтового пакета.
**ЛЕГИТ-пейлоад**: валидный список перков → применится + SaveSettings() = **пишет в конфиг на диск**.
KIК за страйки после ~6 → реконнект.

### E4. FragmentBloat [670] — **МОДУЛЬ: FragmentBloatModule.cs** (P2P-only)
MessageDefragmenter: `partialMessages[id] = new MessageFragment[FragmentCount]`
(FragmentCount ushort ≤65535 ≈ 1MB), чистка только при полном сборе, таймаутов нет.
Только против host-from-game (Lidgren-фрагментация библиотечная).

---

## ★★★★ ВЫСОКИЙ — gameplay-целостность без пермов

### E5. CircuitAmplifier [726] — **МОДУЛЬ: CircuitAmplifierModule.cs (ИЗОБРЕТЁН)**
CircuitBox RenameConnections (опкод 15, энти-событие ComponentState):
сервер бродкастит твой NetDictionary **всем игрокам**. 65535 пар ≈ 256KB → ×N.
★ DoSProtection считает ПАКЕТЫ, не байты: 66/мин × 256KB = 17MB/s вход при легальном рейте.
Каждый клиент жжёт ToDictionary(65535). Гейт: доступ к схембоксу (не Locked).
Дубликаты ключей = ArgumentException ДО бродкаста (защита сервера убила N-вариант).
Попутно: Fabricator indexer-throw, Select* O(n²) 65535 ID, MoveComponent NaN.

### E6. Spectre [723] — **МОДУЛЬ: SpectreModule.cs**
Геттер `SpectatePos` отдаёт значение серверу **только мёртвым/бестелесным**
(Client.cs:115). Мёртвый шлёт ЛОЖНУЮ точку (2 float, 0 валидации) →
1) радар: персонажи в 1000u от точки стримятся клиенту каждый тик (GetPositionUpdateInterval);
2) hull-синк чужих отсеков; 3) **WreckAI-будильник**: isSomeoneNearby проверяет SpectatePos
всех клиентов → врек активен (турели+спавн клеток) пока камера «никого не видит».
Сервер не верифицирует, что камера реально в точке (CWE-602-класс).

### E7. ErrorForge [718-720] — **МОДУЛЬ: ErrorForgeModule.cs, ПОДТВЕРЖДЁН ЖИВЫМ ТЕСТОМ**
ERROR(15) → HandleClientError → MISSING_ENTITY: u16 evId, u16 entId, byte ≤5, 5×string≤16
(сырые строки в errorStr БЕЗ санитизации, CWE-117). Один пакет = текст **ВСЕМ ИГРОКАМ
В ЧАТ** (KickClient → DisconnectClient → SendChatMessage с reason) + консоль сервера
+ файл лога + окна админов (ServerLog perm) + event_error_log_server_*.log.
Кик отправителя безусловен (дизайн) = цена трансляции. Owner-ветка: EndGame(false)!
≤16 симв = чисто; >16 в строке = честный кик malformed. GameStarted обязателен.

### E8. BiomeJump [703-704] — **МОДУЛЬ: BiomeJumpModule.cs**
`Map.SelectLocation(selectedLocIndex)` — БЕЗ AllowDebugTeleport/соседности/репутации
(только bounds). Прыжок в эндгейм-биом/радиацию/враждебный аутпост одним пакетом.
`SetLocation` (curIdx) — гейтнут, не трогаем.

### E9. PhantomCampaign [696b-699] — **МОДУЛЬ: PhantomCampaignModule.cs**
CAMPAIGN_SETUP_INFO isNew=true через фоллбэк ManageRound → `StartNewCampaign`
**перезаписывает GameSession**, множители raw float персистятся в новый сейв
(CampaignSettings: клампы только UI). Формат и поля — в notes 696b/699.

### E10. TalentFlood / TalentOverspend [579-580, 600-603] — **МОДУЛИ: TalentFloodModule.cs, TalentOverspendModule.cs**
UpdateTalents: CanManageTalents = owner-only-check, очки НЕ проверяются → все таланты
бесплатно (порядок prerequisites). Клиника: NetAffliction.Price клиентский (int overflow →
лечение за отрицательную цену), HealAllPending. Respec-цикл FLOOD→REFUND→FLOOD.
46 CharacterAbility классов = полный Ability Engine (GiveMoney/PutItem/SpawnItems...).

### E11. SellByID [683-685] — **МОДУЛЬ: SellByIdModule.cs (3 способа)**
SellItems: оплата при `FindEntityByID=null` (skip удаления, Give выполняется) =
фантом-принтер; itemId чужого предмета = кража; лимит store.Balance; partial purchase.

---

## ★★★ СРЕДНИЙ

12. **[721] VoteKick** — **МОДУЛЬ: VoteKickModule.cs**: единственный безпермовый кик ДРУГОГО:
min(2, eligible×0.6) голосов = **автобан 1ч**; голоса персистят через релогин цели;
self-vote разрешён (проверки нет); оунер/Kick/Ban-пермы иммунны; голосующему 120с+ на сервере.
13. **[722] Possess** — **МОДУЛЬ: PossessModule.cs**: настоящее вселение в монстра НЕВОЗМОЖНО
(IsBot = HumanAIController only, ID монстров вне crew-лукапа, инпут locked к c.Character);
фантом (Character.Controlled) работает; **антидот АФК-кика: любой чат-пакет сбрасывает KickAFKTimer**.
14. **[727] Reactor framing**: blameOnBroken не чистится — последний «виновный» платит
-0.5/с и -30 при meltdown; KickBanThreshold=1 → автокик→бан; карма<20 = принудительный Herpes 100.
15. **[584/646] ConversationAction**: SelectedOption = сырой байт, Options[255] throw КАЖДЫЙ ТИК
вне try → **смерть процесса дедика** (не catch-all!). Гейт: быть TargetClient диалога.
16. **[640] REQUEST_BACKUP_INDICES**: клиентский path → GetFiles + N×декомпрессий синхронно;
на Windows UNC → **NTLM-hash capture**; enumeration oracle. XXE мёртв (DtdProcessing.Prohibit,
XmlResolver null — проверено 728); zip-slip мёртв; CreateSavePath: слэши режутся, точки остаются (1 dir up).
17. **[637] Radio Piracy**: wifi LinkToChat → SendDirectChatMessage, имя источника = имя предмета,
без спам-фильтра (1с cooldown).
18. **[686/688] PropertyChanger** — **МОДУЛЬ: PropertyChangerPlus.cs**: полный каталог InGameEditable
(свет-альфа=невидимый свет, MotionSensor 1000, Delay NaN, Memory, RegEx, Terminal, Wifi, NameTag 32);
plain [Editable] сервер отвергает (OfType<InGameEditable> не матчит наследование — 727 подтвердил
для ButtonTerminal.Signals string[]: **new string[ReadInt32()] жив, но недостижим**).
19. **[688] Admin Dead Menu** — **МОДУЛЬ: AdminDeadMenuModule.cs**: статус-панель фоллбэка
+ SellByID launcher + флаг-пакеты кампании.
20. **[691] PlayerInventoryViewer** — **МОДУЛЬ: PlayerInventoryViewerModule.cs** (рентген экипажа).
21. **[677-680] SelfSpawn** — **МОДУЛЬ: SelfSpawnModule.cs**: второй клиент; один акк = фантом
(Steam auth-сессия); -multiclienttestmode.
22. **[451/449] бюджет EventBomb-предков**: VOIP TOC-дроп батча, Order indexer, BackupIndices oracle —
см. Part 1 (11-13), действуют.
23. **[709] Skill>100**: UpdateSkillsEventData float без клампа — только сервер-сайд, клиенту недостижимо.

---

## ✅ ДОП. ПРОВЕРЕНО ЧИСТЫМ (463-727, негативы для репорта)
XXE (Prohibit+null resolver — 728), zip-slip (IsExtractionPathValid), Lidgren disconnect
base64+enum-parse (инъекция "}Separator[" невозможна — 728), BarotraumaCore.dll (734 строки
метаданных = PacketHeader enums + NetworkMagicStrings + threading — нулевая поверхность),
TransferMoney/REWARD_DISTRIBUTION (кламп 0-100 на проводе), UPDATE_CHARACTERINFO (whitelist
цветов, лимиты), TAKEOVERBOT (Permadeath+PermanentlyDead+crew-scope), FILE_REQUEST (имя+MD5),
READ порогов чата/логов, ServerReadInput (memInput≤60), VoIP (len≤255×BUFFER_COUNT),
Auth-handshake (соль/гомоглифы/баны), BanList (админ-гейт, но int-overflow BitPosition
задокументирован), ConnectionPanel NRE (connectionCount<Count → half-apply),
DoSProtection.clients leak при дисконнекте, Inventory receivedItemIds leak.

## 🧬 КОРНЕВЫЕ ПАТТЕРНЫ (дополнение к Part 1)
8. **«Аллокация до валидации»** (713/714/715): ReadArray клампит — ручные ридеры нет.
   Универсальный fix-класс для Daedalic: clamp ВСЕХ счётчиков ≤65535 + лимит inflate-выхода.
9. **«Декомпрессия/парсинг вне DoS-таймера»** (714): таймер стартует после чтения заголовков.
10. **«Сервер ретранслирует клиентский контент без квоты»** (726): broadcast-примитивы
    нуждаются в байтовой квоте на отправителя.
11. **«Геттеры как ACL»** (723): SpectatePos-щит — правильная идея, но данные все равно
    принимаются и хранятся; доступ мёртвым = дизайн-дыра радара.
12. **«Exception вне buffered-try = смерть процесса»** (584/646): единственный найденный
    путь к SIGKILL дедика — персистентный индекс в событийном объекте.

## 📦 КАТАЛОГ МОДУЛЕЙ (research/mods/, 39 файлов, цель CSHUB)
Готовые к копипасте: EventBomb, DecompBomb, PerkBomb, ErrorForge, Spectre, CircuitAmplifier,
FragmentBloat, VoteKick, Possess, BiomeJump, PhantomCampaign, SellById, TalentFlood,
TalentOverspend(×2), Respec, PaidMedicalClinic, FreeHeal, PropertyChangerPlus,
PlayerInventoryViewer, AdminDeadMenu, SelfSpawn, ServerCrashMenu, OrderSpammer v8,
MoneyTransfer, NegTransfer, CampaignHijack, LagBubble, ItemSwap, PermEscalationTest(закрыт)...

## 🔬 ВЕРИФИКАЦИЯ БИНАРНИКА [725]
Декомпил юзера (github.com/overbaffe1/BarotraumaMod, SERVER/DedicatedServer):
**1.13.4.0+0d380afc** — ВСЕ 6 топ-находок 1:1 (токены: ReadPerks 0x060035FB,
ServerEntityEventManager.Read 0x06003475, ReadOnlyMessage 0x0600371D, Client.SpectatePos:415,
HandleClientError:1147→KickClient:1325). 27/27 ServerRead-классов = сорсфорку.
Клиентский dll юзера = LuaCS-модифицированный (не ваниль). BarotraumaCore.dll = enums+threading.

*Part 2 сформирован волной 728. Полные разборы: research/RESEARCH_NOTES.txt (до 25000+ строк).*
