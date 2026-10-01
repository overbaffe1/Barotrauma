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
