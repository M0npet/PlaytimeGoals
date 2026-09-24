# PlaytimeGoals

[English](README.md) | **Українська**

PlaytimeGoals — користувацький плагін для ArchiSteamFarm, який додає
цілі ігрового часу для окремих ігор, підтримку Steam Families та
безпечну роботу зі Steam Family View.

> **Статус:** вихідний код v0.5.0. Основний backend, Steam Family,
> відновлення Family View, міграція, IPC/TLS та вебінтерфейс уже
> протестовані. Додаткові реальні runtime-тести ще тривають.

## Можливості

- Єдина бібліотека OWN + Steam Family
- Позначення джерела: OWN, FAMILY та OWN+FAMILY
- Окрема кінцева ціль ігрового часу для кожної гри
- Необмежений idle через значення `null`
- До 32 одночасно керованих AppID
- Контроль доступності сімейних копій
- Відстеження запущених ігор членами Steam Family
- Реальна гра на ПК має вищий пріоритет
- ASF CardsFarmer має пріоритет під час фармінгу карток
- Steam Family View залишається глобально увімкненим
- Тимчасово дозволяються лише ігри активного batch
- Точне відновлення станів ABSENT / ALLOW / DENY
- Crash-safe журнал відновлення Family View
- Fail-closed recovery gate
- Кешування Steam-бібліотеки для вебінтерфейсу
- Live polling лише коли сторінка видима
- Пошук і фільтрація бібліотеки
- Штатний idle-власник ASF вимикається під час роботи PlaytimeGoals

## Перевірене середовище

PlaytimeGoals v0.5.0 розроблявся та тестувався з:

- ArchiSteamFarm 6.3.9.6
- SteamKit2 3.4.0
- .NET 10
- ASF-ui на Vue 2.7

Новіші версії ASF можуть потребувати змін у коді.

## Конфігурація

PlaytimeGoals додає такі поля до конфігурації бота:

```json
{
  "GamesPlayedWhileIdle": [],
  "CustomGamePlayedWhileIdle": null,
  "PlaytimeGoalsEnabled": true,
  "PlaytimeGoalsParentalWritesEnabled": true,
  "PlaytimeGoalsBatchSize": 5,
  "PlaytimeGoals": {
    "123456": 100,
    "234567": null
  }
}
```

Ключі `PlaytimeGoals` — це єдиний список ігор, якими керує плагін.

- Числове значення — ціль загального ігрового часу в годинах.
- `null` — необмежений idle.

Коли PlaytimeGoals увімкнений, `GamesPlayedWhileIdle` повинен бути
порожнім, а `CustomGamePlayedWhileIdle` — `null`, інакше кілька
компонентів можуть одночасно керувати Steam GamesPlayed.

## Steam Family View

Запис у Family View можна вимкнути.

Якщо `PlaytimeGoalsParentalWritesEnabled` увімкнений:

1. ASF уже повинен уміти штатно розблоковувати Steam Family View.
2. Перед зміною PlaytimeGoals зберігає точний початковий custom-стан.
3. Тимчасово дозволяється лише поточний активний batch.
4. Після виходу гри з batch початковий стан відновлюється.
5. Після restart/reconnect незавершене відновлення виконується до нового
   managed idle.

PIN Family View береться з конфігурації бота, яка вже знаходиться в
пам'яті ASF. PlaytimeGoals не записує PIN у власні state-файли.

Ніколи не додавайте ASF-конфіг або Family View PIN до Git.

## Steam Families

Плагін об'єднує власні ігри акаунта з іграми, доступними через Steam
Families.

Для FAMILY-only гри idle дозволяється лише тоді, коли відомо, що
сімейна копія вільна. OWN-ігри залишаються доступними незалежно від
сімейної копії.

Якщо стан сімейної копії невідомий, FAMILY-only гра не запускається.

## Пріоритет

Керований idle навмисно має нижчий пріоритет за реальну гру.

Порядок:

1. Реальна гра у Steam
2. ASF CardsFarmer
3. PlaytimeGoals

Плагін звільняє GamesPlayed замість конкуренції з іншою Steam-сесією.

## Збірка з вихідного коду

Репозиторій зараз розрахований на інтеграцію в дерево вихідного коду
ArchiSteamFarm 6.3.9.6.

Клонуйте відповідний реліз ASF разом із submodules:

```bash
git clone --recursive \
  --branch 6.3.9.6 \
  https://github.com/JustArchiNET/ArchiSteamFarm.git
```

Скопіюйте каталог `PlaytimeGoals/` з цього репозиторію в корінь
вихідного коду ASF поруч із `ArchiSteamFarm/`.

Каталог `ASF-ui/` є overlay. Скопіюйте його файли на відповідні шляхи
у checkout ASF.

Зберіть плагін:

```bash
dotnet build \
  PlaytimeGoals/PlaytimeGoals.csproj \
  -c Release \
  --no-restore \
  --no-dependencies
```

DLL буде створено тут:

```text
PlaytimeGoals/bin/Release/net10.0/PlaytimeGoals.dll
```

Для модифікованого ASF-ui:

```bash
cd ASF-ui
npm ci
npm run build
```

Після цього встановіть DLL у каталог плагінів ASF і розгорніть
зібраний ASF-ui відповідно до способу встановлення вашого ASF.

Готового packaged installer/release поки немає.

## Безпека

Ніколи не додавайте до репозиторію:

- конфігурації ботів ASF
- Steam логіни або паролі
- Steam login keys
- SteamID або дампи акаунта
- PIN Steam Family View
- пароль ASF IPC
- cryptkey
- приватні TLS-ключі
- cookies або auth tokens
- бази даних ASF
- runtime-логи
- state-бази PlaytimeGoals
- recovery journal PlaytimeGoals

Детальніше: [SECURITY.md](SECURITY.md).

## Ліцензія

Проєкт поширюється за Apache License 2.0.

ASF-ui overlay містить змінені файли з проєкту ASF-ui, який також
поширюється за Apache License 2.0. Див. [NOTICE](NOTICE).

## Відмова від відповідальності

PlaytimeGoals — незалежний користувацький плагін і офіційно не
пов'язаний з Valve, Steam або проєктом ArchiSteamFarm.
