# Типографика ArkCopyBuffer

Выбор от 2026-10-06: **IBM Plex Sans + IBM Plex Mono**, статические Regular и SemiBold. Решение принято после сравнения в WPF на синтетических русских/английских карточках, конфигурациях, путях и командах в светлой и тёмной темах.

## Почему прежние карточки различались

Обычный текст наследовал Segoe UI Variable Text 14 DIP, а `IsMono` переключал код на Cascadia Mono 13 DIP. На этом компьютере WPF действительно находил Segoe Variable и Cascadia из Windows Terminal: это не подмена на Consolas. Различались одновременно семейство, размер и метрики. Дополнительно плитки использовали 12/11 DIP, полный hover — 13/12 DIP.

Распознавание кода остаётся эвристическим: пути и многострочный текст с характерными отступами/окончаниями строк. Моноширинность сохраняет выравнивание и различимость синтаксиса; обычному тексту удобнее пропорциональный набор.

## Исследованные варианты

| Семейства | Результат для этой программы |
|---|---|
| Segoe UI / Segoe UI Variable + Cascadia Mono | Хорошая привычная Windows-пара, но различаются метрики; Cascadia зависит от установленного шрифта. WPF пока не предоставляет полноценного управления variable axes. |
| Segoe UI + JetBrains Mono | Хороший вариант с системным UI; увеличенная высота строчных Mono требует оптической настройки размера. |
| Source Sans 3 + JetBrains Mono | Source Sans создан для UI, обе семьи поддерживают кириллицу. На малом размере Source Sans выглядит спокойнее и мельче, Mono — плотнее. |
| IBM Plex Sans + IBM Plex Mono | Совпадают высоты букв, базовая линия и метрики строки; согласованные формы в обеих темах. Выбран для минимального скачка между текстом и кодом. |
| Noto Sans + JetBrains Mono | Хорошее языковое покрытие, но в этом компактном интерфейсе преимущества перед Plex не обнаружено. |

Измерения `GlyphTypeface` для Regular (доля em, не субъективная оценка читабельности):

| Семейство | x-height | cap-height | высота строки |
|---|---:|---:|---:|
| Segoe UI Variable Text / Segoe UI | 0.500 | 0.700 | 1.330 |
| Cascadia Mono | 0.518 | 0.693 | 1.162 |
| Source Sans 3 | 0.486 | 0.660 | 1.326 |
| JetBrains Mono | 0.550 | 0.730 | 1.320 |
| IBM Plex Sans / Mono | 0.516 | 0.698 | 1.300 |
| Noto Sans | 0.536 | 0.714 | 1.362 |

Визуальное предпочтение Plex — оценка для наших карточек, а не утверждение, что этот шрифт универсально лучше для зрения.

## Правила приложения

- Общие ресурсы `TextFont` и `MonoFont` в `App.xaml`; иконки/математические символы используют свои системные семьи.
- История: оба семейства 14 DIP, строка 20 DIP. Плитки: 12/15. Полное текстовое превью: 13/20. Редактор: 16 DIP для обоих семейств. У специальных компактных командных превью свой размер 12 DIP.
- Основной текст Regular, заголовки SemiBold из настоящих статических файлов. Размер определяется ролью текста; смена на Mono сама по себе размер не уменьшает.
- Панель, редактор, объединение, диалоги, меню и подсказки используют `TextFormattingMode.Display` и округление компоновки. ClearType принудительно не включается поверх прозрачных слоёв: WPF сам выбирает допустимое сглаживание.
- Четыре неизменённых TTF занимают 750792 байта до упаковки; системная установка не требуется. OFL и происхождение файлов сохранены в `Assets/Fonts/` и доступны в дистрибутиве.

Offscreen-образцы созданы в 96/120/144 DPI. Они проверяют ресурсы, глифы и компоновку без показа окон. Это не проверка физического монитора, его ClearType-настройки или реального перехода между мониторами с разным DPI.

## Первичные источники

- [Microsoft: Windows typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography) — одна UI-семья, роль размеров и начертаний, поддержка языков.
- [WPF: variable fonts #7758](https://github.com/dotnet/wpf/issues/7758) — открытый запрос поддержки variable fonts.
- [IBM: Typeface](https://www.ibm.com/design/language/typography/typeface/) и [Type scale](https://www.ibm.com/design/language/typography/type-scale/) — система Sans/Mono, кириллица, размеры и строки.
- [Adobe: Source Sans](https://github.com/adobe-fonts/source-sans) — UI-назначение, статические файлы и лицензия.
- [JetBrains Mono](https://www.jetbrains.com/lp/mono/) — высота строчных, различимость символов, кириллица.
- [Microsoft: Cascadia](https://github.com/microsoft/cascadia-code) — Mono без лигатур, статические/variable варианты.
- [Noto Latin/Greek/Cyrillic](https://github.com/notofonts/latin-greek-cyrillic) — языковое покрытие и лицензия.
- [WPF: ClearTypeHint](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.renderoptions.cleartypehint?view=windowsdesktop-10.0) и [упаковка шрифтов](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/packaging-fonts-with-applications) — сглаживание прозрачных слоёв и встроенные шрифты.
