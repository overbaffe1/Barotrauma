# research/ — INDEX

> Рабочая директория аудита. Главный документ: **[FINAL_DIGEST.md](FINAL_DIGEST.md)**.

---

## 📖 Документы

| Файл | Что это |
|---|---|
| **[FINAL_DIGEST.md](FINAL_DIGEST.md)** | Финальный аудит. Part 1 (волны 2–462), Part 2 (463–727). Все CVE, форматы пакетов, паттерны. |
| [RESEARCH_NOTES.txt](RESEARCH_NOTES.txt) | ~25 000 строк сырых заметок по каждой волне. Хронологический лог. |
| [FUNNY_MOMENTS.txt](FUNNY_MOMENTS.txt) | Юмор-лог (по одной шутке на волну). |
| [RESEARCH_LOG.txt](RESEARCH_LOG.txt) | Авто-генерённый per-file coverage report. |
| [COVERAGE_PROGRESS.txt](COVERAGE_PROGRESS.txt) | Статистика: done/partial/not started. |
| [ALL_CS_PATHS.txt](ALL_CS_PATHS.txt) | Список всех 2531 `.cs` файлов репозитория. |

## 📊 Данные

| Файл | Что это |
|---|---|
| [paths.csv](paths.csv) | CSV: `path,lines,status,explored` — сетка покрытия. |
| [ranges.json](ranges.json) | JSON: `path → [a-b, c-d, ...]` — какие диапазоны строк прочитаны. |

## 🐍 Утилиты

```bash
# Отметить прочитанные диапазоны файла
python3 research/mark.py "<relpath-from-repo-root>" "a-b,c-d,e-f"

# Пересобрать RESEARCH_LOG.txt из ranges.json
python3 research/generate.py

# Построить индекс
python3 research/build_index.py
```

## 📦 mods/ — 39 CSHUB-модулей

См. основной [README.md](../README.md#-researchmods--cshub-модули) для полного
каталога по категориям (живые тесты / крит / permissionless / deprecated).

## 🗄️ Архив

| Файл | Что это |
|---|---|
| `pastebin_1_*.txt` `pastebin_2_*.txt` `pastebin_3_*.txt` | Снапшоты прошлых диалогов (восстановление контекста). |

## 📜 Соглашение по именам волн

Каждая волна аудита имеет номер (например, wave 715). Соответствие:
- Wave 2–462: см. Part 1 of FINAL_DIGEST.md
- Wave 463–727: см. Part 2 of FINAL_DIGEST.md
- Wave 728+: см. свежие коммиты `git log --oneline`

Номер волны всегда фигурирует в commit message и заголовке блока
в RESEARCH_NOTES.txt.
