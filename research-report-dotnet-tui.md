# Исследование: Terminal UI библиотеки для .NET

**Дата исследования:** 2026-06  
**Источники:** веб-поиск и анализ официальных страниц (GitHub, документации, статьи)

---

## Краткий вывод

В экосистеме .NET есть несколько библиотек для создания терминальных интерфейсов (TUI — Terminal User Interface). Они делятся на две категории:

1. **Библиотеки форматированного вывода** — красивый вывод в консоль (таблицы, графики, промпты), но без интерактивного UI с окнами.
2. **Полноценные TUI-фреймворки** — оконные системы, виджеты, обработка ввода, как у настольных GUI-приложений, но в терминале.

---

## Основные библиотеки

### 1. Spectre.Console

| Параметр | Значение |
|---|---|
| **GitHub** | [spectreconsole/spectre.console](https://github.com/spectreconsole/spectre.console) |
| **Документация** | https://spectreconsole.net/ |
| **Звёзды GitHub** | ~11,490 |
| **NuGet-загрузки** | ~44.4 млн |
| **Контрибьюторов** | ~146 |
| **Последняя версия** | 0.57.0 (pre-1.0) |
| **.NET** | net8/9/10 + .NET Standard 2.0 |
| **Лицензия** | MIT |
| **Возраст** | ~6 лет |

**Что это:** Библиотека для создания красивых консольных приложений — форматированный вывод, таблицы, графики, деревья, промпты, прогресс-бары, живые дисплеи. 40+ виджетов (Table, Calendar, BarChart, Progress, Tree, Panel, Prompt, Status и др.).

**Архитектура:** Потоковый вывод в stdout. Нет экранного буфера, нет композитинга, нет интерактивного UI с окнами. Это «принтер» — красивый вывод, который прокручивается.

**Сильные стороны:**
- Лучший выбор для красивого CLI-вывода (таблицы, графики, деревья)
- Промпты (Selection, MultiSelect, Text) с валидацией
- 80+ стилей спиннеров, live-дисплеи, прогресс-бары
- Разметка `[red bold]text[/]` (Spectre Markup)
- CLI-фреймворк (Spectre.Console.Cli) для командных приложений
- NativeAOT-совместимость
- Огромная база пользователей и загрузок

**Слабые стороны:**
- Нет интерактивного UI (окна, фокус, модальные диалоги)
- Нет обработки мыши
- Нет экранного буфера / композитинга
- Промпты блокирующие (по одному за раз)

**Когда использовать:** Красивый вывод CLI-инструментов, таблицы, графики, промпты, прогресс-бары. Не для полноценных TUI-приложений.

---

### 2. Terminal.Gui

| Параметр | Значение |
|---|---|
| **GitHub** | [tui-cs/Terminal.Gui](https://github.com/tui-cs/Terminal.Gui) |
| **Документация** | https://tui-cs.github.io/Terminal.Gui/ |
| **Звёзды GitHub** | ~11,060 (11.1k) |
| **NuGet-загрузки** | ~1.8 млн |
| **Контрибьюторов** | ~130 (199 по другим данным) |
| **Последняя версия** | 2.4.5 (v2 GA) |
| **.NET** | .NET 10 (v2.4.x) |
| **Лицензия** | MIT |
| **Возраст** | ~8.5 лет (с 2017) |

**Что это:** Самый зрелый и широко используемый кросс-платформенный TUI-тулкит для .NET. Создан Miguel de Icaza (создатель Mono, Xamarin, GNOME). Полноценный фреймворк с оконной системой, виджетами, обработкой ввода.

**Архитектура:** Классическая однопоточная event loop модель (как WinForms). Один главный цикл обрабатывает ввод, layout и отрисовку. Фоновая работа маршализуется через `Application.Invoke()`. Единый общий буфер с painter's algorithm (back-to-front).

**Сильные стороны:**
- Самый зрелый TUI-фреймворк в .NET, проверен в продакшене
- Самый широкий набор виджетов: Button, TextField, TextView, ListView, TableView, TreeView, TabView, MenuBar, ComboBox, CheckBox, ProgressBar, LineCanvas, HexView, GraphView, ColorPicker, DatePicker, Wizard, CharMap, FlagSelector и др.
- v2 GA: перекрывающиеся, перемещаемые и изменяемые окна
- Полная поддержка мыши (click, drag, wheel) и клавиатуры
- TrueColor с автоматическим fallback на 16 цветов
- Темы, цвета, key bindings — настраиваемые и сохраняемые
- Markdown-виджет (Markdig), File/Directory browsers
- In-line или full-screen режим (как Claude Code / Copilot CLI)
- Шаблоны проектов: `dotnet new install Terminal.Gui.Templates`
- Большое сообщество, много ресурсов и ответов на StackOverflow

**Слабые стороны:**
- Нет inline-разметки (стилизация через ColorScheme/VisualRole)
- Нет per-cell alpha blending / композитинга
- Нет встроенного терминального эмулятора (PTY)
- Нет видео
- NativeAOT — в процессе восстановления (v2)

**Когда использовать:** Большинство .NET TUI-приложений — формы, диалоги, конфигурационные UI, файловые браузеры, полноэкранные приложения. Если нужно максимальное сообщество и зрелость.

**Пример кода:**
```csharp
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

using IApplication app = Application.Create();
app.Init();

using Window window = new() { Title = "Hello World (Esc to quit)" };
Label label = new()
{
    Text = "Hello, Terminal.Gui v2!",
    X = Pos.Center(),
    Y = Pos.Center()
};
window.Add(label);
app.Run(window);
```

---

### 3. SharpConsoleUI

| Параметр | Значение |
|---|---|
| **GitHub** | [nickprotop/ConsoleEx](https://github.com/nickprotop/ConsoleEx) |
| **Документация** | https://nickprotop.github.io/ConsoleEx/docfx/_site/ |
| **Звёзды GitHub** | ~232 |
| **NuGet-загрузки** | ~14,000 |
| **Контрибьюторов** | 1 (solo) |
| **Последняя версия** | 2.4.77 |
| **.NET** | net8/9/10 |
| **Лицензия** | MIT |
| **Возраст** | ~16 месяцев |

**Что это:** Оконная консольная UI-система с композитором. «Десктоп в терминале» — перекрывающиеся окна с drag, resize, minimize, maximize, taskbar, title bar chrome. Построен поверх Spectre.Console.

**Архитектура:** Многопоточная композиторная модель. Каждое окно имеет свой async-поток, обновляется независимо. DOM-пайплайн Measure → Arrange → Paint (вдохновлён WPF/Avalonia). Per-window CharacterBuffer, true compositor с per-cell RGBA alpha blending (Porter-Duff), occlusion culling, double buffering, 3-level dirty tracking, ~60 FPS.

**Сильные стороны:**
- Полноценный оконный менеджер: перекрывающиеся окна, drag/resize/minimize/maximize, taskbar (Alt+1-9), always-on-top, модальные стеки
- Композитор: per-cell RGBA alpha blending, градиентные фоны, blur, fade, row-анимации
- Уникальные контролы: TerminalControl (PTY-эмулятор терминала), VideoControl (видео!), CanvasControl (30+ примитивов рисования), SparklineControl, BarGraphControl
- Разметка Spectre везде: `[red bold]text[/]` работает в 26 контролах (кнопки, списки, деревья, ячейки таблиц, меню, статус-бары)
- `[markdown]` тег — Markdown доступен в любом markup-контроле, не в одном виджете
- Кликабельные ссылки `[link=url]` с событием LinkClicked (приложение решает, что делать)
- 13 встроенных синтаксических подсветчиков
- MVVM data binding (Bind/BindTwoWay, INotifyPropertyChanged)
- Плагинная архитектура (темы, контролы, окна, сервисы)
- NativeAOT-совместимость
- Spectre.Console-интеграция: любой IRenderable как контрол

**Слабые стороны:**
- Solo-проект (1 контрибьютор), малое сообщество
- Мало загрузок на NuGet (~14K)
- Нет flex-аллокатора (пропорциональность через Star-tracks в Grid)
- Dock только вертикальный (Top/Bottom), нет Left/Right
- Нет source-generated reactive bindings (как у XenoAtom)
- Нет ColorPicker, HexView, Wizard
- Синтаксические подсветчики regex-based, не grammar-based (менее точные)

**Когда использовать:** Мультиоконные дашборды, real-time анимации, композиторные эффекты, встроенный терминал, видео в терминале, расширение Spectre.Console оконной системой.

**Реальные приложения на нём:** ServerHub (мониторинг Linux-серверов), LazyNuGet (NuGet-менеджер), LazyDotIDE (.NET IDE с LSP).

---

### 4. XenoAtom.Terminal.UI

| Параметр | Значение |
|---|---|
| **GitHub** | (поиск не нашёл прямую ссылку; автор — Alexandre Mutel) |
| **Звёзды GitHub** | ~271 |
| **NuGet-загрузки** | ~17,800 |
| **Контрибьюторов** | ~2 |
| **Последняя версия** | 3.7.4 |
| **.NET** | .NET 10 only |
| **Лицензия** | BSD-2-Clause |
| **Возраст** | ~5 месяцев |

**Что это:** Реактивный UI-фреймворк для терминала — «WPF для терминала». Создан Alexandre Mutel (Markdig, SharpDX, Scriban, бывший член .NET Foundation TSG).

**Архитектура:** Single CellBuffer + diff. Реактивные bindings с auto-invalidation. Самая продвинутая цветовая система — true RGBA alpha blending в linear color space (sRGB-linear LUT). Synchronized output (DEC 2026) против мерцания.

**Сильные стороны:**
- Реактивные bindings `[Bindable]` (source-generated, auto-dependency tracking) — самый продвинутый
- Flexbox + Grid + Dock layout (VStack, HStack, WrapStack, Grid, DockLayout, ZStack, Splitter, ScrollViewer) — самая мощная layout-система
- Широкий набор контролов: DataGridControl (виртуализированный, sort/filter/search/edit), TreeView, TabControl, MenuBar + CommandPalette, LogControl, Sparkline, BarChart, LineChart, Breakdown, Accordion, Collapsible, Slider, CommandBar
- Markdown (Markdig), TextMate-подсветка (TextMateSharp — grammar-based, точнее regex)
- Терминальная графика: Kitty/Sixel/iTM2 image rendering
- 73 файла стилей на контрол, Theme система
- NativeAOT-ориентированный дизайн
- Screenshot/snapshot testing

**Слабые стороны:**
- Очень молодой проект (~5 месяцев)
- Малое сообщество (2 контрибьютора, ~271 звёзд)
- Только .NET 10
- Нет полноценного оконного менеджера (только WindowLayer с z-ordered overlays, popups, dialogs)
- Нет встроенного терминального эмулятора (PTY)
- Нет видео
- BSD-2-Clause лицензия (не MIT)

**Когда использовать:** Реактивные TUI с MVVM-bindings, если нужна самая мощная layout-система (flexbox/dock), терминальная графика (Kitty/Sixel), и вы на .NET 10.

---

### 5. Другие notable библиотеки

| Библиотека | Звёзды | Описание | Лицензия |
|---|---|---|---|
| **Console Framework** | ~556 | Кросс-платформенный TUI-тулкит на концепциях WPF: XAML, data binding, routed events, commands | Apache-2.0 |
| **Consolonia** | ~479 | TUI-фреймворк на базе Avalonia UI: XAML, data binding, анимация, стили | MIT |
| **TUI.NET Core** | ~37 | Простой TUI-фреймворк: window management, checkbox, listbox, color schema, pagination | MIT |
| **ConsoleDraw** | — | TUI-библиотека с поддержкой кастомных контролов | MIT |
| **SharpAnvil** | — | Консольный фреймворк для игр: ASCII/ANSI art, спрайты, анимации | MIT |

---

## Сравнительная таблица: ключевые возможности

| Возможность | Spectre.Console | Terminal.Gui v2 | XenoAtom.Terminal.UI | SharpConsoleUI |
|---|---|---|---|---|
| **Тип** | Форматированный вывод | Forms-based TUI | Reactive TUI | Windowed TUI + compositor |
| **Окна (перекрытие)** | Нет | Да (v2 GA) | Popups only | Да (лучший) |
| **Drag/Resize/Min/Max** | Нет | Да (v2) | Нет | Да (встроено) |
| **Мышь** | Нет | Click, drag, wheel | Click, drag, wheel, hover | Click, drag, wheel, double-click, hover |
| **Модальные диалоги** | Промпты | Да | Dialog + Backdrop | True modal stack |
| **Inline разметка** | Да (вывод) | Нет | Своя (не Spectre) | Да (Spectre-compatible, везде) |
| **Markdown** | Нет | Да (виджет) | Да (MarkdownControl) | Да ([markdown] везде) |
| **Alpha blending** | Нет | Нет | Да (linear) | Да (Porter-Duff per-cell) |
| **Композитор** | Нет | Painter's alg. | Single buffer + diff | True compositor |
| **Встроенный терминал (PTY)** | Нет | Нет | Нет | Да |
| **Видео** | Нет | Нет | Нет | Да |
| **Терминальная графика (Kitty/Sixel)** | Нет | Нет | Да | Да (Kitty) |
| **Data binding** | Нет | Manual/events | Reactive [Bindable] | MVVM (Bind/BindTwoWay) |
| **Layout** | Measure/Render | Pos/Dim | Flexbox + Grid + Dock | DOM (Measure/Arrange/Paint) |
| **NativeAOT** | Да | В процессе | Да | Да |
| **.NET Standard 2.0** | Да | Нет (v2) | Нет | Нет |
| **Плагины** | Нет | Нет | Нет | Да |

---

## Рекомендации по выбору

### Нужен красивый CLI-вывод (таблицы, графики, промпты)?
→ **Spectre.Console** — безоговорочный лидер для этого.

### Нужен полноценный TUI (формы, диалоги, файловые браузеры)?
→ **Terminal.Gui** — самый зрелый, широкий набор виджетов, большое сообщество.

### Нужны реактивные bindings и мощный layout (flexbox/dock)?
→ **XenoAtom.Terminal.UI** — самый продвинутый reactive + layout (но только .NET 10, молодой).

### Нужны мультиоконные дашборды, real-time анимации, композиторные эффекты, встроенный терминал?
→ **SharpConsoleUI** — уникальные возможности (compositor, PTY, видео), но solo-проект.

### Нужна совместимость со старым .NET (Standard 2.0)?
→ **Spectre.Console** (единственный с .NET Standard 2.0).

### Нужна максимальная стабильность и сообщество?
→ **Terminal.Gui** или **Spectre.Console** — тысячи пользователей, годы battle-testing.

---

## Источники

1. **Spectre.Console** — официальная документация: https://spectreconsole.net/ (v0.57)
2. **Terminal.Gui** — GitHub: https://github.com/tui-cs/Terminal.Gui (11.1k stars, v2.4.5 GA)
3. **SharpConsoleUI** — сравнительная статья: https://dev.to/nikolaos_protopapas_d3bd6/building-terminal-uis-in-net-how-sharpconsoleui-complements-terminalgui-hb9 (март 2025)
4. **Сравнение 4 библиотек** — SharpConsoleUI docs: https://nickprotop.github.io/ConsoleEx/docfx/_site/COMPARISON.html
5. **Каталог C# TUI фреймворков** — https://web-reference.org/en/catalog/frameworks/tui/csharp/
6. **awesome-tuis** — https://github.com/rothgar/awesome-tuis
7. **LibHunt сравнения** — https://www.libhunt.com/compare-Terminal.Gui-vs-spectre.console

---

*Отчёт создан автономным ассистентом на основе веб-исследования.*