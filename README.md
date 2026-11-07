# KeidoScreen

Приёмник повтора экрана iPhone/iPad для Windows 10/11 (x64): «Пункт управления →
Повтор экрана» на iPhone, и экран появляется на компьютере. Без iTunes и Bonjour.

- **Качество** — разрешение берётся по самому большому монитору (вплоть до 4K), H.265, 60 к/с.
- **Задержка** — аппаратное декодирование и режим без буферизации.
- **Подстройка под ПК** — при первом запуске программа проверяет, какие декодеры
  реально работают (Direct3D 12, NVIDIA NVDEC, Direct3D 11, процессор), и выбирает лучший.
  Если видеокарта или драйвер поменялись, проверка повторяется. Если декодер падает
  во время работы, программа сама переключается на следующий.
- **Громкость** — отдельный ползунок и кнопка «без звука» (только для KeidoScreen).
- **Автообновление** — через GitHub Releases с проверкой SHA-256.

## Установка
Скачайте `KeidoScreen-Setup-X.Y.Z.exe` со страницы [Releases](../../releases/latest).
Либо portable-архив: распакуйте его и запустите `KeidoScreen.exe`.

## Сборка
Нужна Windows 10/11 x64. Окно собирается встроенным в Windows компилятором C#
(.NET Framework 4.x), движок — в MSYS2.

1. Один раз на машине сборки ставятся MSYS2, GStreamer, NSIS и gh, затем собирается UxPlay:
   ```
   powershell -ExecutionPolicy Bypass -File packaging\build_engine.ps1
   ```
2. По желанию соберите минимальный FFmpeg (LGPL, сборка выходит примерно в 4 раза меньше):
   ```
   C:\msys64\usr\bin\bash.exe -lc "bash packaging/build_ffmpeg_min.sh /c/msys64/src/pkg/ffmpeg-min"
   ```
3. Соберите релиз:
   ```
   powershell -ExecutionPolicy Bypass -File build_release.ps1 -Version 1.1.0
   ```
   Флаг `-SkipEngine` пропускает пересборку движка: это быстро, если менялось только окно.

Скрипт собирает движок (только нужные плагины GStreamer и их DLL) и лицензии,
компилирует окно, **проверяет движок без MSYS2 в PATH**, затем делает установщик,
portable-zip и `SHA256SUMS.txt`. Всё это попадает в `release/`.

## Выпуск обновления
1. В `repo.txt` — репозиторий вида `owner/KeidoScreen`. Он вшивается в программу,
   и по нему она ищет обновления.
2. Один раз выполните `gh auth login`.
3. Соберите и опубликуйте релиз:
   ```
   powershell -ExecutionPolicy Bypass -File build_release.ps1 -Version 1.1.1
   powershell -ExecutionPolicy Bypass -File publish_release.ps1 -Version 1.1.1 -Notes "Что нового"
   ```
Установленные копии раз в 6 часов проверяют `releases/latest` и показывают баннер
«Доступно обновление». Установщик скачивается, сверяется с `SHA256SUMS.txt`
и ставится тихо (`/S /RUN`), после чего программа перезапускается.
Portable-копия только открывает страницу релиза.

## Структура
```
src/MainWindow.xaml     интерфейс (WPF, тёмная тема), вшивается в exe
src/MainWindow.cs       логика окна, трей, запуск движка, состояния
src/Core.cs             настройки, подбор декодера под ПК, план запуска uxplay
src/AudioVolume.cs      громкость процесса движка (Windows Core Audio)
src/Updater.cs          автообновление из GitHub Releases
packaging/              движок, установщик NSIS, лицензии, иконка, тестовые клипы
build_release.ps1       сборка релиза
publish_release.ps1     публикация релиза на GitHub
```

## Лицензия
GPL-3.0 (как у UxPlay). Сторонние компоненты и ссылки на их исходники —
в `licenses/THIRD_PARTY_NOTICES.txt` внутри сборки.
iPhone, iPad и AirPlay — товарные знаки Apple Inc.; KeidoScreen не связан с Apple.
