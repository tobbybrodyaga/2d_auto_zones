# LiraSlabZones (2d_auto_zones)

Автозоны дополнительного армирования плит: **ЛИРА-САПР 2024 → превью → Revit 2023**.

Зона создаётся там, где подобранный **As1…As4** превышает фон (см²/м). В Revit размещаются семейства **SUM-30…34** (`SUM-30-Зона дополнительного армирования_R22`).

Ориентиры UX: [SmartRebar](https://promcore.io/blog/smartrebar/), [Smart КР](https://rutube.ru/video/f4198009b09324c7316e697a16613820/).

Подробный алгоритм: [`Шаблон/ALGORITHM.md`](Шаблон/ALGORITHM.md)  
Рабочие заметки / история правок: [`Шаблон/README.md`](Шаблон/README.md)

---

## Требования

- Windows x64
- .NET Framework 4.8 / SDK с поддержкой `net48`
- ЛИРА-САПР 2024 (COM: LiraSapr, LiraResAPI, Sapfir) — для анализа схемы
- Autodesk Revit 2023 — для add-in и размещения зон

Сборка **только x64**: `-p:Platform=x64`.

---

## Структура

```
.
├── README.md                          ← этот файл
├── .gitignore
├── rebar_extra_reinforcement_engine.py
├── rebar_layout_api.py
├── run_slider_demo.py
└── Шаблон/
    ├── LiraSlabZones.sln
    ├── ALGORITHM.md
    ├── README.md
    ├── install-addin.ps1 / .bat
    ├── families/                      ← RFA SUM-30…
    ├── interop/                       ← LiraSapr / LiraResAPI / Sapfir Interop
    ├── config/                        ← settings.json (локально, не в репо)
    ├── output/                        ← рабочие JSON (локально, не в репо)
    ├── tools/
    └── src/
        ├── LiraSlabZones.Core/        ← бизнес-логика (без WPF/Revit)
        ├── LiraSlabZones.PreviewHost/ ← standalone WPF-превью
        ├── LiraSlabZones.Revit2023/   ← add-in + UI
        └── LiraSlabZones.Exporter/    ← консоль → JSON
```

**UI править только в** `Шаблон/src/LiraSlabZones.Revit2023/UI/`.  
PreviewHost линкует эти файлы — вторую копию XAML не создавать.

---

## Быстрый старт

### 1. Сборка

```bat
cd /d "путь\к\репо\Шаблон"
dotnet build LiraSlabZones.sln -c Debug -p:Platform=x64
```

### 2. Превью без Revit

```bat
src\LiraSlabZones.PreviewHost\bin\x64\Debug\net48\LiraSlabZones.PreviewHost.exe
```

### 3. Add-in Revit 2023

```bat
powershell -ExecutionPolicy Bypass -File .\install-addin.ps1
```

Перезапуск Revit → вкладка **LiraSlabZones**.

**Перед «Анализ ЛИРА»:** схема открыта в ВИЗОРе, виден нужный фрагмент, арматура плит подобрана.

---

## Пайплайн

```
LIRA COM (LiraSapr) + LiraResAPI
  → геометрия / оси / отметки
  → только горизонтальные плиты
  → As1…As4
  → зоны As > фон (мозаика → пики → SUM-3)
  → превью
  → размещение в Revit
```

Единицы: координаты ЛИРА — **м**; Ø / шаг / длины зон — **мм**; As / фон — **см²/м**.  
В футы переводит только `ZonePlacer` при вставке в Revit.

---

## Локальные данные (не в Git)

В репозиторий **не** входят JSON и артефакты сборки (см. `.gitignore`):

| Путь | Назначение |
|------|------------|
| `Шаблон/config/settings.json` | настройки анализа |
| `Шаблон/output/*.json` | выгрузки зон / пластин |
| `**/bin/`, `**/obj/` | сборка |

После клона восстанови `settings.json` и при необходимости положи рабочие JSON в `output/`.

---

## Python (демо / прототип)

В корне лежат скрипты прототипа раскладки:

- `rebar_extra_reinforcement_engine.py`
- `rebar_layout_api.py`
- `run_slider_demo.py`

Основная рабочая реализация — C# в `Шаблон/`.

---

## Лицензия / использование

Внутренний инструмент отдела КР. Перед публикацией во внешний репозиторий проверьте лицензии interop-DLL и семейств RFA.
