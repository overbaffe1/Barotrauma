# Barotrauma — Research Fork

> Форк ванильного репозитория Barotrauma, используемый как рабочая копия для
> **аудита сетевых доверительных границ** (client → server / server → client).
>
> **Не для контрибуции в апстрим.** Оригинал: [FakeFish/Barotrauma](https://github.com/FakeFish/Barotrauma).
> Лицензия игры: [EULA.txt](EULA.txt). Весь исследовательский код в `research/` —
> наш, для приватного использования и security-репорта.

---

## 🗺️ Карта репозитория

```
Barotrauma/
├── README.md                        ← ты здесь
├── EULA.txt                         ← лицензия игры (не наша)
├── CONTRIBUTING.md                  ← оригинальный contribution guide
├── промпт.txt                       ← emergency-prompt для продолжения сессии
├── промт.txt.backup / старые        ← легаси (можно чистить)
│
├── Barotrauma.dll                   ← декомпилированный клиент 1.13.4.0 (референс)
├── BarotraumaCore.dll               ← core lib (enums, threading, msg primitives)
├── DedicatedServer.dll              ← декомпилированный dedicated server 1.13.4.0
│
├── Barotrauma/                      ← ИСХОДНИКИ ИГРЫ
│   ├── BarotraumaClient/            ← код клиента (рендер, GUI, UI)
│   │   ├── Networking/              ← ClientPeer, GameClient, пайплайн приёма
│   │   ├── Events/                  ← ClientApply для event-ов
│   │   ├── Characters/              ← позиции, health, animation
│   │   ├── Items/                   ← Item.ClientReadPosition, PhysicsBody
│   │   ├── Physics/                 ← PhysicsBody.ClientRead (IsValid проверки)
│   │   └── ...
│   │
│   ├── BarotraumaServer/            ← код сервера ← ЦЕЛЬ АУДИТА #1
│   │   ├── Networking/              ← GameServer.ReadDataMessage (диспетчер),
│   │   │   │                          ServerPeer (Lidgren/P2P), Voting, Karma,
│   │   │   │                          BanList, ChatMessage, ServerSettings,
│   │   │   │                          FileSender, RespawnManager, RateLimiter
│   │   │   ├── NetEntityEvent/      ← ServerEntityEventManager ← EventBomb (715)
│   │   │   └── Primitives/Peers/    ← Lidgren/P2P peer-ы, DoS protection
│   │   ├── Items/Components/        ← ServerEventRead всех машин:
│   │   │   ├── Machines/            ← Engine/Reactor/Pump/Fabricator/Steering/Decon
│   │   │   ├── Signal/              ← Wire/Wifi/Terminal/CircuitBox/ConnectionPanel
│   │   │   ├── Power/               ← PowerContainer/PowerDistributor
│   │   │   ├── Holdable/            ← Holdable attach (unit mismatch 150x)
│   │   │   └── ...
│   │   ├── Characters/              ← CharacterNetworking, KarmaManager
│   │   ├── GameSession/             ← Campaign, MedicalClinic, CargoManager, Wallet
│   │   ├── Events/                  ← EventManager.ServerRead, Mission, Traitor
│   │   ├── Utils/                   ← DoSProtection, RateLimiter
│   │   └── DebugConsole.cs          ← двойной гейт + rate limiter
│   │
│   ├── BarotraumaShared/            ← общий код (клиент + сервер) ← ЦЕЛЬ #2
│   │   ├── SharedSource/
│   │   │   ├── Networking/          ← ClientPacketHeader enum, NetSettings,
│   │   │   │                          Primitives/Message (IReadMessage,
│   │   │   │                          Message.cs ← readonly msg/decompress),
│   │   │   │                          INetSerializableStruct (ReadArray клампы)
│   │   │   ├── NetStructBitField.cs ← ReadOnlyBitField (bit-field injection!)
│   │   │   ├── Utils/NetStructs/    ← NetCollection/NetPair/NetDictionary/NetLimitedString
│   │   │   ├── Serialization/       ← XMLExtensions (XXE-safe), SerializableProperty
│   │   │   ├── Characters/          ← Character.SetStun/CanHear, CharacterInfo
│   │   │   ├── Items/               ← Item.ReadPropertyChange (ChangeProperty канал)
│   │   │   ├── Events/              ← EventManager, EventSet, 68 EventActions
│   │   │   ├── GameSession/         ← CampaignMode (AllowedToManageCampaign), Mission
│   │   │   ├── StatusEffects/       ← StatusEffect.Apply (TargetType cat.)
│   │   │   └── Utils/               ← SaveUtil (GetIndexData file-oracle),
│   │   │                              SegmentTable, ToolBox
│   │   └── ...
│   └── ...
│
├── Libraries/                       ← сторонние либы
│   ├── BarotraumaLibs/BarotraumaCore/  ← PacketHeader/NetworkEnums core
│   ├── Lidgren.Network/             ← NetBitWriter (ReadBytes — EventBomb корень!)
│   ├── Concentus/                   ← OpusDecoder (VOIP TOC bomb)
│   └── Farseer Physics Engine 3.5/  ← физика
│
└── research/                        ← НАША РАБОЧАЯ ДИРЕКТОРИЯ (см. ниже)
```

---

## 🔬 research/ — что тут

| Файл / директория | Назначение |
|---|---|
| **`FINAL_DIGEST.md`** | **ГЛАВНЫЙ ДОКУМЕНТ.** Полный аудит волн 2–462 (Part 1) + 463–727 (Part 2). Все CVE с форматами пакетов, корневые паттерны, каталог чистых/грязных каналов. |
| `RESEARCH_NOTES.txt` | Полные логи 700+ волн, ~25k строк. Сырые заметки по каждому ServerRead. |
| `FUNNY_MOMENTS.txt` | Юмор-лог по волнам. |
| `RESEARCH_LOG.txt` | Авто-генерённый per-file coverage (генерится `generate.py`). |
| `ALL_CS_PATHS.txt` | Список всех 2531 .cs файлов. |
| `paths.csv` + `ranges.json` | Какие диапазоны каждого файла уже разобраны (для `mark.py`). |
| `COVERAGE_PROGRESS.txt` | Статистика покрытия. |
| `mark.py` | Отметить прочитанный диапазон: `python3 research/mark.py <relpath> "a-b,c-d"` |
| `generate.py` | Пересобрать `RESEARCH_LOG.txt` из `ranges.json`. |
| `build_index.py` | Утилита индексации. |
| `pastebin_*.txt` | Снапшоты диалогов. |
| `mods/` | **39 C#-модулей для CSHUB** (см. ниже). |

---

## 📦 research/mods/ — CSHUB модули

Хост: **CSHUB** (Workshop id 2880887340). Каждый `.cs` = один модуль.
Установка: `C:\Users\<user>\AppData\Local\Daedalic Entertainment GmbH\Barotrauma\WorkshopMods\Installed\2880887340\CSharp\Client\Modules\`

### ⭐ Живые тесты (подтверждено на реальном сервере)
| Модуль | Волна | Что делает |
|---|---|---|
| `EventBombModule.cs` | 715 | Unbounded `new byte[N]` в энти-событийном конверте → 2GB/пакет alloc. 6-зарядный баш-бластер. |
| `ErrorForgeModule.cs` | 718-720 | CWE-117: сырой текст из пакета попадает в лог сервера/всем в чат как причина кика. |

### ⭐⭐ Крит-эксплойты
| Модуль | Волна | Что делает |
|---|---|---|
| `DecompBombModule.cs` | 714 | DEFLATE-бомба: unbounded inflate, время разжатия **вне DoS-таймера** → SIEGE 66/с ~4GB/с без кика. Lidgren/P2P. |
| `PerkBombModule.cs` | 713 | `Identifier[varint]` без клампа в `ReadPerks`. Гейт: сервер без активных пермщиков. Модификаторы/персист в конфиг. |
| `FragmentBloatModule.cs` | 670 | P2P-only: `MessageFragment[FragmentCount]` = 1MB/packet, без таймаута. Lidgren — фрагментация библиотечная. |
| `CircuitAmplifierModule.cs` | 726 | CircuitBox RenameConnections бродкастит словарь всем → 256KB × N players amplification, DoS-каунт слеп к объёму. |

### ⭐ Gameplay / гриф (permissionless)
| Модуль | Волна | Что делает |
|---|---|---|
| `BiomeJumpModule.cs` | 703 | `Map.SelectLocation` без AllowDebugTeleport (locked-коннекты тоже работают). |
| `SpectreModule.cs` | 723 | Мёртвый SpectatePos = радар карты + будильник таламуса. |
| `NegTransferModule.cs` | 590 | Отрицательный перевод (money printer) через вотчину. |
| `MoneyTransferModule.cs` | 588 | Базовый перевод + вотчина. |
| `TalentFloodModule.cs` | 580 | Все таланты бесплатно. |
| `TalentOverspendModule.cs` / `TalentOverspend.cs` | 600-611 | Оверспенд талантов + все 46 CharacterAbility. |
| `RespecModule.cs` | 656+ | Respec-цикл (FLOOD→REFUND→FLOOD). |
| `FreeHealModule.cs` / `PaidMedicalClinicModule.cs` | 453/600 | Клиника: любой аффликция (в т.ч. не healable), цена = 0. |
| `SellByIdModule.cs` / `SellFakeItemsModule.cs` | 683-685 | Продажа несуществующих предметов / чужих предметов / вечный баланс магазина. |
| `OrderSpammerModule.cs` | 566+ | Спам TargetAllCharacters ордеров (deconstruct). |
| `ItemSwapModule.cs` | 570+ | Клиентский spawn/copy предметов. |
| `VoteKickModule.cs` | 721 | Автоматизация kick-vote (мин. 2 голоса = автобан 1ч). |
| `PossessModule.cs` | 722 | Фантом-вселение в монстра (клиент-сайд) + анти-AFK. |
| `LagBubbleModule.cs` | 458/541 | «Все лагают, я нет» — resync-запрос в лобби = сервер льёт весь бэклог. |
| `ResyncCyclerModule.cs` | 458 | Цикл ENDROUND_SELF + resync = сервер льёт историю постоянно. |
| `WireStarModule.cs` | 447 | Holdable attach unit-mismatch: 150x дистанция через стены. |
| `PropertyChangerPlus.cs` | 686+ | Полный InGameEditable-каталог: свет/сонар/двигатель/sensor/свет. |
| `PropertyEditorModule.cs` | 688 | Редактор [Editable] полей. |
| `AdminDeadMenuModule.cs` | 688b | Меню при мёртвом админе: send-flag packet для всех кампейн-операций. |
| `CampaignHijackModule.cs` | 587 | PROBE/DrainBank/SwipeWallets/FireAllBots в одном клике. |
| `PhantomCampaignModule.cs` | 699 | CAMPAIGN_SETUP_INFO isNew=true → перезапись активной кампании. |
| `BlockStartModule.cs` | 457 | Блок старта раунда через RESPONSE_CANCEL_STARTGAME. |
| `ServerCrashMenuModule.cs` | 584 | Меню 30 методов краша сервера. |
| `SettingsFlipperModule.cs` | 586 | Togglekarma/Патдаун/etc через admin fallback. |
| `SaveCorrupterModule.cs` | 640 | Запрос сейва для корруптинга (REQUEST_BACKUP_INDICES). |
| `PermEscalationTest.cs` | 695 | Тест перм-гейтов (закрыт сервером, только для отчёта). |
| `PacketDumpModule.cs` | 440s | Hex-дамп пакетов для декодирования. |
| `PlayerInventoryViewerModule.cs` | 691 | Рентген инвентаря экипажа. |
| `SelfSpawnModule.cs` | 677-680 | Запуск второго клиента (фантом). |
| `TalentBudgetSelfTest.cs` | — | Unit-test талантов. |

### Deprecated / исторические
- `PermEscalationTest.cs` — сервер закрыл эскалацию (695).

---

## 📊 Топ-CVE кратко

| # | Класс | Корень | Сила |
|---|---|---|---|
| 1 | **Unbounded alloc** | `ReadBytes(varint-2)` / `new Identifier[varint]` / `DeflateStream unbounded` | 6-byte packet = 1-2GB RAM |
| 2 | **Money printer** | NetAffliction.Price клиентский (int overflow) + NegativeTransfer vote | Отрицательная цена = баланс += X |
| 3 | **CWE-117 log injection** | HandleClientError пишет сырые строки клиента в лог | Доверие к user input |
| 4 | **Amplification** | DoSProtection считает ПАКЕТЫ, не байты/стоимость | CircuitAmplifier 256KB→N×M |
| 5 | **Unit mismatch** | Holdable attach: sim vs display units (×100) | 150x дистанция attach |
| 6 | **NaN injection** | Pump/Wifi/ReadPropertyChange без IsValid | Отравление энергосети |
| 7 | **Trust asymmetry** | ClientReader (bounds checks), ServerReader (нет) | EventBomb envelope |
| 8 | **Dead-admin fallback** | `AllowedToManageCampaign` возвращает true при пустом/мёртвом сервере | Все Manage* команды |

Полный разбор: [research/FINAL_DIGEST.md](research/FINAL_DIGEST.md).

---

## 🛠️ Работа с репо

```bash
# Проверить покрытие конкретного файла
grep '"<relpath>"' research/ranges.json

# Отметить прочитанное
python3 research/mark.py "Barotrauma/BarotraumaServer/ServerSource/..." "10-50"

# Пересобрать лог
python3 research/generate.py

# Запушить (часто нужен rebase из-за снапшотов)
git fetch origin arena/01a03e3d-barotrauma
git merge-base --is-ancestor origin/arena/01a03e3d-barotrauma HEAD \
  && git push origin arena/01a03e3d-barotrauma \
  || { git branch -f w tmp && git reset --hard origin/arena/... -q \
       && git checkout w -- . && git add -A && git commit -m "..." \
       && git branch -D w && git push origin arena/...; }
```

**Ветка сессии:** `arena/01a03e3d-barotrauma`

---

## 🔗 Связанное

- [BarotraumaMod](https://github.com/overbaffe1/BarotraumaMod) — декомпил официального билда 1.13.4.0 (клиент + DedicatedServer), используется для diff-верификации.
- CSHUB (Workshop 2880887340) — мод-хост, куда грузятся модули.
- LuaCsForBarotrauma (Workshop 2559634234) — Lua/C# хост.

## 📝 Disclaimer

Код в `research/` предназначен для приватного security-исследования и security-репорта разработчикам Barotrauma. Использование против чужих серверов без разрешения владельца **незаконно**.
