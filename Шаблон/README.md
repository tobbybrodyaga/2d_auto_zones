# LiraSlabZones — README для Cursor

> **Дата актуализации:** 2026-07-17  
> Читать **целиком** перед правками. Продолжать работу **поверх** текущей логики, не переписывая ядро без явного запроса.

## Назначение

**ЛИРА-САПР 2024 → зоны доп. армирования плит → превью → Revit 2023**

Семейство: `SUM-30-Зона дополнительного армирования_R22`  
Зона создаётся, где подобранный **As1…As4** превышает **фон** (см²/м).

UX-ориентиры: [SmartRebar](https://promcore.io/blog/smartrebar/#wbb1), [Smart КР](https://rutube.ru/video/f4198009b09324c7316e697a16613820/).

---

## Быстрый старт

```bat
cd /d "C:\Users\Filippov_G\Pictures\Test\Шаблон"
dotnet build LiraSlabZones.sln -c Debug -p:Platform=x64
src\LiraSlabZones.PreviewHost\bin\x64\Debug\net48\LiraSlabZones.PreviewHost.exe
```

Add-in Revit 2023:

```bat
powershell -ExecutionPolicy Bypass -File .\install-addin.ps1
```

Перезапуск Revit → вкладка **LiraSlabZones**.

**Перед «Анализ ЛИРА»:** схема открыта в ВИЗОРе, виден нужный фрагмент, арматура плит подобрана.  
Сборка **только x64** (`-p:Platform=x64`).

---

## Структура репозитория

```
Шаблон/
  README.md                 ← этот файл
  LiraSlabZones.sln
  install-addin.ps1 / .bat
  config/settings.json      ← дефолты анализа
  families/*.rfa
  interop/                  ← LiraSapr / LiraResAPI / Sapfir Interop
  output/                   ← примеры JSON
  src/
    LiraSlabZones.Core/           бизнес-логика (без WPF/Revit)
    LiraSlabZones.PreviewHost/    standalone WPF (линкует UI из Revit2023)
    LiraSlabZones.Revit2023/      add-in + UI
    LiraSlabZones.Exporter/       консоль → JSON
```

**UI править только в** `src/LiraSlabZones.Revit2023/UI/`.  
PreviewHost **линкует** эти файлы — вторую копию XAML не создавать.

---

## Пайплайн (не ломать)

```
LIRA COM (LiraSapr) + LiraResAPI
  → LiraGeometryReader   узлы, пластины, оси, отметки
  → ContourFix           порядок узлов КЭ (без ромбов/бантиков)
  → MeshBoundary         только гориз. плиты (все узлы на одном Z)
                         уровни / ближайшая отметка
  → LiraReinforcementReader   As1…As4 (батчи, soft-fail)
  → SlabZoneAnalyzer     зоны As > фон, JSON, смена уровня
  → PreviewViewport      превью
  → ZonePlacer           Revit
```

### Core — файлы

| Файл | Роль |
|------|------|
| `Models.cs` | DTO, `AnalysisSettings`, оси, уровни |
| `LiraGeometryReader.cs` | COM-таблицы геометрии / осей / отметок |
| `LiraReinforcementReader.cs` | Чтение As |
| `ContourFix.cs` | Угловая сортировка контура, габариты по рёбрам |
| `MeshBoundary.cs` | `IsHorizontalPlate`, уровни Z, внешний контур |
| `SlabZoneAnalyzer.cs` | `Analyze`, `BuildResult`, `RebuildForElevation`, `SaveJson`, `SaveAllPlatesJson` |
| `DemoSlabFactory.cs` | Демо-плита без ЛИРА |
| `AnalysisSettingsStore.cs` | Загрузка/сохранение settings |
| `SolutionPaths.cs` | Поиск корня solution |

### Revit / UI — файлы

| Файл | Роль |
|------|------|
| `UI/ZonePreviewWindow.xaml(.cs)` | Всё окно настроек и действий |
| `UI/PreviewViewport.cs` | Векторный холст: сетка, изополя, зоны, оси, transform, текст |
| `App` / `Commands.cs` / `OpenZonePreviewCommand.cs` | Лента Revit |
| `ZonePlacer.cs` | Вставка семейств |
| `FamilyLoader.cs` | Загрузка RFA |

Единицы: координаты ЛИРА в **метрах**; Ø/шаг/длины зон в **мм**; As/фон в **см²/м**.
В Revit длины м/мм → футы (`× 3.280839895`) **только** в `ZonePlacer`. Core не хранит футы.

Автораскладка: `ZoneLayoutEngine` (мозаика As−фон → пики → SUM-30…34, направление As1/As3=X, As2/As4=Y, ширина кратна шагу).
UI: фон As, слайдер детализации, шаг 100/200, класс бетона.

---

## Зафиксированное поведение (что уже сделано)

### 1. Геометрия КЭ
- Пластины = КЭ с 3–4 узлами (стержни исключены).
- Контур упорядочивается в `ContourFix` (сырой TypeAndNodes давал ромбы).
- Габариты зоны — по рёбрам, не AABB.
- Сетка на превью: светлая заливка + тонкие линии.
- Галочка «Контур плиты + серый фон» **удалена**.
- Тёмная обводка Outline и обводка зон **не рисуются** (давали «чёрные линии» поверх КЭ).  
  `Outline` всё ещё считается для статистики/JSON.

### 2. Только горизонтальные плиты + уровни
- В анализ/уровни: пластины, у которых **все узлы на одном Z** (допуск **5 мм**) — `MeshBoundary.IsHorizontalPlate`.
- Стены/наклонные отсекаются.
- UI: «Загрузить уровни из ЛИРЫ» → комбо → «Взять плиту ближайшую к уровню».
- Причина: авто-доминантный этаж брал не ту отметку (напр. +29 вместо +55).
- `AnalysisResult.AllPlates` — все гориз. плиты всех уровней (`[JsonIgnore]` в обычном SaveJson).
- As читается на **все** уровни сразу (смена отметки не теряет As).
- Методы: `RebuildForElevation`, `CollectLevels`, `FilterNearestLevel`.

### 3. As1…As4

| Слой | Обычно |
|------|--------|
| As1 | Нижняя, направление 1 |
| As2 | Нижняя, направление 2 |
| As3 | Верхняя, направление 1 |
| As4 | Верхняя, направление 2 |

Единица: **см²/м**. Зона: `As − фон > ~0.01`.

### 4. Дефолты (актуальные)

| Параметр | Значение |
|----------|----------|
| Фон As1…As4 | **0.1** |
| Мин/макс ширина, мин длина, мин КЭ | **0** (0 = без фильтра / без max) |
| `VisualizationScale` (шаг изополей) | **0** |
| Show As1, As2 | on; As3, As4 | off |
| Сетка КЭ | on |
| Изополя | off |
| Оси из ЛИРЫ | **off** |

Синхронно в: `config/settings.json`, `Models.AnalysisSettings`, XAML.

### 5. Ползунок изополей (`VisualizationScale`)
- Диапазон **0…25** см²/м.
- **0:** цвета зон/изополей = легенда **As1…As4** (изополя — по слою с max As).
- **> 0:** цвет по `floor(As / step)` из **уникальной палитры без повторов** (`DistinctBandBrush`).
- На расчёт зон и Revit **не влияет**.
- Движение ползунка сразу обновляет превью (`SettingsChanged` → `RefreshTransform`).

### 6. Оси
- Таблица `ConstructionAxes`: имя/тип/координата или отрезок; мм→м; дедуп; диагностика в журнале.
- По умолчанию галочка **выкл**.
- Рисунок: кружки **сверху** (верт. оси) и **слева** (гориз.), одна линия; штрих **только снаружи** до края плиты, не сквозь контур.

### 7. Transform (Revit)
- Offset X/Y, Rotation; ↺/↻ ±90°; «Выровнять по осям»; сброс.
- Реально применяется в `PreviewViewport` (`Tx` / `UnTx` / pivot).

### 8. Текст на холсте
- Мир: `Scale(s, -s)` → без компенсации текст вверх ногами.
- Исправлено: `DrawUprightText`.

### 9. JSON
- «Сохранить JSON зон…» → `SaveJson` (зоны текущего уровня).
- «Сохранить JSON всех плит…» → `SaveAllPlatesJson` (все гориз. плиты всех уровней в один файл).

### 10. Отметка
- Бейдж «Отметка КЭ: …» на холсте + строка в статистике справа.

### 11. Производительность / устойчивость
- `ModelPart: Visible` — текущий видимый фрагмент (не вся модель).
- Батчевое чтение As, soft-fail при ошибках API.
- Векторный `PreviewViewport` (зум без растрового размытия).
- LOD при большом числе КЭ.

---

## Карта UI (левая панель)

1. Источник: Демо / JSON / Анализ ЛИРА  
2. Слои As1…As4 (галочки показа)  
3. Фон армирования As1…As4  
4. Параметры зон + шаг изополей  
5. Отметка / уровень (комбо + 2 кнопки)  
6. Сопоставление: сетка / изополя / оси / смещение / поворот  
7. Действия: пересчёт / JSON зон / JSON всех плит / Revit  

Холст: зум колесом, пан ПКМ, клик по зоне → детали справа.

---

## Рабочий процесс пользователя

1. Открыть схему в ЛИРА (видимый фрагмент нужного этажа).  
2. PreviewHost или Revit → превью.  
3. «Загрузить уровни» → выбрать отметку → «Взять плиту ближайшую…».  
4. Фон As, слои; при необходимости шаг изополей.  
5. Смещение/поворот под Revit.  
6. Разместить / сохранить JSON.

---

## Куда править

| Задача | Файл |
|--------|------|
| Цвета, оси, зум, текст, iso | `PreviewViewport.cs` |
| Кнопки, уровни, дефолты полей | `ZonePreviewWindow.xaml(.cs)` |
| Фильтр стен / Z | `MeshBoundary.cs` |
| Порядок узлов КЭ | `ContourFix.cs` |
| Зоны, Analyze, JSON | `SlabZoneAnalyzer.cs` |
| COM ЛИРА | `LiraGeometryReader.cs` |
| As API | `LiraReinforcementReader.cs` |
| Revit размещение | `ZonePlacer.cs`, `FamilyLoader.cs` |
| Дефолты | `config/settings.json` + `Models.AnalysisSettings` + XAML |

После правок:

```bat
dotnet build LiraSlabZones.sln -c Debug -p:Platform=x64
powershell -ExecutionPolicy Bypass -File .\install-addin.ps1
```

---

## Чего не делать

1. Не отключать фильтр «все узлы на одном Z» — стены снова забьют уровни.  
2. Не возвращать серый контур-фон и толстую обводку Outline/зон без просьбы.  
3. Не рисовать оси сквозь плиту и кружки с двух сторон.  
4. Не дублировать UI в PreviewHost.  
5. Не менять ProgID COM и имя семейства Revit без нужды.  
6. `VisualizationScale` — это **шаг As (см²/м)**, не множитель контраста; **0 = цвета As1–As4**.  
7. Только **x64**, не AnyCPU/x86.  
8. `AllPlates` большой; в обычный zones-JSON не сериализуется.  
9. Не делать «большой рефакторинг ради красоты» без запроса.

---

## Известные нюансы

- Парсер осей эвристический → смотреть `axes rows=… ok=…` в журнале.  
- Сопоставление с Revit — ручное (offset/rotation), без автопривязки к осям Revit.  
- Нет managed `LiraAPI.dll` — только COM Interop в `interop/`.  
- ЛИРА bin: `C:\Program Files (x86)\LIRA SAPR\LIRA SAPR 2024\Bin\x64`.  
- Архив для передачи: при необходимости `LiraSlabZones_for_Cursor.zip` рядом с проектом (собирать без `bin/`/`obj/`).

---

## Команды

```bat
dotnet build LiraSlabZones.sln -c Debug -p:Platform=x64
dotnet build src\LiraSlabZones.PreviewHost\LiraSlabZones.PreviewHost.csproj -c Debug -p:Platform=x64
powershell -ExecutionPolicy Bypass -File .\install-addin.ps1
```

---

## Инструкция агенту Cursor

1. Прочитать этот README целиком.  
2. Менять **точечно** под запрос.  
3. Сохранять текущие дефолты и фильтры, пока не попросят иначе.  
4. После UI/Core — пересобрать x64 (+ install-addin для Revit).  
5. Этот файл — **источник правды** по состоянию на дату в шапке; при существенных изменениях поведения — **обновить README**.
