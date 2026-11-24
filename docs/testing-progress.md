# Игровой стенд CasualtiesCoop

## Baseline и исследование

До правок `Verify.ps1` успешно собрал Release и выполнил ProtocolChecks, ArchitectureChecks и NetworkChecks. Нативные точки входа подтверждены по локальной `Assembly-CSharp.dll`. SHA-256 сборки из рабочего дерева и игры Steam совпал с хешем в отчётах. См. [таблицу bindings](game-test-bindings.md).

До изменений пользователь проверил движение, прыжки, повороты и звуки на двух Steam-клиентах и сообщил о раздельном повреждении блоков, отсутствии эффекта когтей у второго игрока и проблеме с бутылкой спирта. Результаты ниже получены после правок в одном процессе с виртуальным транспортом.

## Этапы 1–4

- NetworkChecks и Unity Runner используют один TestKit с управляемым временем и трассировкой сети. Проверка архитектуры контролирует зависимости production-сборок.
- Отдельный BepInEx-плагин запускает JSON-сценарии в штатном процессе Unity через Steam. В тестовом режиме Steam-лобби не создаётся.
- Конкурентный подбор проверяет одного владельца в реестре, согласие независимых гостевых хранилищ и деактивацию реального предмета на хосте. Реальный слот гостя проверяется отдельным запуском с отдельным предметом-проекцией. Так сохраняется production-семантика удалённого подбора.
- Первый запуск гонки закончил сценарий за 4,08 с с результатом PASS. Оболочка записала FAIL из-за чтения кода завершения после закрытия процесса. Runner теперь удерживает дескриптор процесса с момента подтверждения PID. Повторные запуски получили PASS в Unity и в оболочке. [Первичный отчёт Unity](../artifacts/game-tests/a4ae2ededc0b49b2a6c682313448a4af/player-result.json)
- Видимый запуск с четырьмя участниками и seed 73 прошёл. Управление паузой, шагом и скоростью реализовано, ручная проверка этих клавиш не проводилась.

## Воспроизведение

Из каталога `mode`:

```powershell
powershell.exe -NoProfile -File ./scripts/Verify.ps1 -DotnetPath 'C:/Program Files/dotnet/dotnet.exe'
powershell.exe -NoProfile -File ./scripts/Verify.ps1 -DotnetPath 'C:/Program Files/dotnet/dotnet.exe' -GameTests
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/items/concurrent-pickup-four-peers.json -Seed 73 -Visible -NoBuild -TimeoutSeconds 75
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/wrong-pickup-count.json -NoBuild -TimeoutSeconds 75
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/undelivered-pickup.json -NoBuild -TimeoutSeconds 75
```

Итоговый `Verify.ps1 -GameTests` собрал Release без предупреждений, выполнил три сборки проверок и 12 игровых сценариев. Контрольные сценарии отдельно вернули код 1. Пакет из контрольных проверок успешно подтвердил оба ожидаемых отказа.

| Проверка | Результат | Отчёт |
| --- | --- | --- |
| Конкурентный подбор, два виртуальных гостя | PASS, один владелец | [UNITY_HOST](../artifacts/game-tests/38de70aa7c384e2287f6a4888c0b44ac/result.json) |
| Подбор, хост и три виртуальных гостя | PASS, один владелец | [UNITY_HOST](../artifacts/game-tests/1a51698b28ca468586db9a126c1714e1/result.json) |
| Четыре участника, видимое окно, seed 73 | PASS | [UNITY_HOST](../artifacts/game-tests/b82e42ace8f14ef3b7f3703e70999466/result.json) |
| Базовый запуск Sandbox | PASS | [UNITY_HOST](../artifacts/game-tests/c12ddb8711f74ac3a36886337345d8f5/result.json) |
| Проекция предмета в фактический слот гостя | PASS | [UNITY_GUEST](../artifacts/game-tests/44e941ab7fc948d1bbfec67fdd28440f/result.json) |
| Реальный `Body.PickUpItem` через production Harmony-обработчик | PASS | [UNITY_HOST](../artifacts/game-tests/9203c019a17e44e6a1e8ef190e060b45/result.json) |
| Бутылка `alcohol` в слоте, бросок и появление в мире у гостя | PASS | [UNITY_GUEST](../artifacts/game-tests/302220ee24ab46efa82fb63db34dcc54/result.json) |
| Общий урон блока, разрушение и входящий эффект когтей | PASS, хост и оба виртуальных гостя увидели 25 урона; найден реальный `ClawAnim` | [UNITY_HOST](../artifacts/game-tests/91d420f5571c465d96992de04ccfea95/result.json) |
| Трещина и разрушение на гостевой проекции | PASS | [UNITY_GUEST](../artifacts/game-tests/fdb427abf29e4ef6b948266354f203fd/result.json) |
| ASCII-карта, запись и чтение реальных блоков | PASS | [Unity](../artifacts/game-tests/2ca447842ecd4d97b2f01706b7bd1e60/result.json) |
| Два ванильных Body и переход в ragdoll | PASS | [Unity](../artifacts/game-tests/7542400a78f94c2489d7622d6a1df703/result.json) |
| Запись и чтение клетки жидкости через `FluidManager` | PASS | [Unity](../artifacts/game-tests/a09ba210af6c4e76b5639550807418ff/result.json) |
| Реальный пещерный клещ и смена цели AI | PASS | [Unity](../artifacts/game-tests/75492c4fd5af436ab86f9ea59bcfed2a/result.json) |
| Намеренно неверное ожидаемое число подборов | FAIL, ожидали 0, фактически 1 | [Отрицательный контроль](../artifacts/game-tests/30c259e66adf406ea48571f96fdc36f3/result.json) |
| Доставка запроса отключена после постановки в очередь | FAIL по тайм-ауту условия, ожидали 1, фактически 0 | [Отрицательный контроль](../artifacts/game-tests/9737459bdf7c4fc2a674e77258e3b60e/result.json) |

`result.json` содержит итог, проверенные условия, seed, хеши сборок и сетевую трассу. В том же каталоге лежат исходный `player-result.json`, лог BepInEx и `isolation.json`. Во всех 12 финальных прогонах подтверждены неизменность игровых настроек и сохранения, а также восстановление затронутых PlayerPrefs. Runner проверил хеши DLL при развёртывании и восстановлении прежнего комплекта.

## Этап 5 и границы покрытия

- Изменения terrain, когтей и консольной выдачи `alcohol` объявлены в lobby protocol 11. Форматы кадров предметов сохранились. Проверка бутылки использует настоящий инвентарь и production-проекцию; полный путь от команды консоли до другого Steam-клиента остаётся открытым.
- При восстановлении terrain хост повторно раздаёт до 4096 запомненных клеток. Долгий сеанс за пределами этой истории в игровых сценариях не проверялся.
- Переподключение, корреляция и повтор снимка, сходимость жидкостей проверены в NetworkChecks. Сквозь отдельные игровые миры и Unity-процессы они в этом стенде не проверены.
- **BLOCKED** — Steam E2E требует второго реального клиента и аккаунта. На текущем стенде остаются непроверенными консольная выдача спирта, отправка эффекта из реального взмаха и полная синхронизация трещин между двумя Steam-клиентами.
- **BLOCKED** — сетевой урон, смерть и добыча мобов: подтверждён локальный binding цели AI, репликация жизненного цикла моба отсутствует. Программный полноценный ввод нескольких персонажей остаётся открытым из-за глобального `PlayerCamera.main.body`. Несколько настоящих Body используют один Unity-мир; каждый виртуальный участник имеет собственное сетевое состояние.


## Расширение стенда с NativeActor

Перед этим этапом `Verify.ps1 -GameTests` прошёл 12 прежних положительных сценариев. Дополнительные исследования сверили `Body.FixedUpdate/Update/Jump/PickUpItem/ThrowItem`, `PlayerCamera.SpawnCharacter/HandleCameraPosition`, `SpiderHandler.Update`, `FluidManager.IncrMove/SimulationStep` и `WaterPusher.OnTriggerStay2D` по штатной сборке.

Ранние прогоны обнаружили два дефекта самого стенда. `Body.Start` перезаписывал физиологию (ожидали 85 hunger, получили 110), поэтому её подготовка теперь откладывается до следующего кадра. Ванильный `HandleCameraPosition` перезаписывал позицию наблюдения, поэтому выбор камеры применяется на выходе из этого нативного метода. Ожидаемые значения сценариев сохранились. [Первый отказ физиологии](../artifacts/game-tests/018b5b0307204f7da2e93b62be3c2454/result.json) · [Первый отказ камеры](../artifacts/game-tests/334f86adf97f4ceda22fed7a7362a9b8/result.json)

### Команды итогового прогона

Из каталога `mode`:

```powershell
powershell.exe -NoProfile -File ./scripts/Verify.ps1 -DotnetPath 'C:/Program Files/dotnet/dotnet.exe' -GameTests
powershell.exe -NoProfile -File ./scripts/Verify.ps1 -DotnetPath 'C:/Program Files/dotnet/dotnet.exe' -GameTests -Visible
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/wrong-pickup-count.json -NoBuild -Visible -HoldOnFailure -HoldOnFailureSeconds 2 -TimeoutSeconds 75
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/undelivered-pickup.json -NoBuild -TimeoutSeconds 90
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/replica-condition-divergence.json -NoBuild -TimeoutSeconds 90
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/terrain-history-limit.json -NoBuild -TimeoutSeconds 90
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Catalog -ProbeItem alcohol -ProbeMob cavetick -NoBuild -TimeoutSeconds 75
```

Итоговый `Verify.ps1 -GameTests -Visible` вернул 0. Release и Testing собраны без предупреждений и ошибок. ProtocolChecks, ArchitectureChecks и NetworkChecks прошли. Выполнен 21 положительный Unity-сценарий и 132 проверок, включая все 12 прежних сценариев. Во всех положительных отчётах `isolation.json` подтвердил сохранность сохранения, настроек и двух затронутых PlayerPrefs.

| Сценарий | Результат и число проверок | Что наблюдали | Отчёт |
| --- | --- | --- | --- |
| `world.actor-native-actions` | PASS, 13 | Подготовка физиологии и конечностей, скорость, приседание, прыжок, реальный подбор и бросок, ragdoll | [result.json](../artifacts/game-tests/4fb1115f14594851a794e1a54e8964e7/result.json) |
| `items.alcohol-throw` | PASS, 7 | Прежняя регрессия | [result.json](../artifacts/game-tests/2d64ef2a790c4272b5bec1acaf187349/result.json) |
| `world.body-ragdoll` | PASS, 4 | Прежняя регрессия | [result.json](../artifacts/game-tests/8be85f2f0dee40d5a0073586c25213c9/result.json) |
| `items.concurrent-pickup` | PASS, 8 | Прежняя регрессия | [result.json](../artifacts/game-tests/ce5112780c3741bfbdc667484004d06a/result.json) |
| `items.concurrent-pickup-four-peers` | PASS, 8 | Прежняя регрессия | [result.json](../artifacts/game-tests/35737596d5cc44a085de7f33ae47dab6/result.json) |
| `world.fluid-cell` | PASS, 3 | Прежняя регрессия | [result.json](../artifacts/game-tests/a10641fe1c3040449cfa13c9b79a225b/result.json) |
| `world.fluid-item-flow` | PASS, 4 | Нативный WaterPusher изменил скорость и предмет сместился по потоку | [result.json](../artifacts/game-tests/192d6b87547c490a9d9abd034881c73e/result.json) |
| `world.fluid-reservoir` | PASS, 5 | Разрушенная перегородка и вода в прежней сухой клетке | [result.json](../artifacts/game-tests/5a8a8cf6ea864761871cd9a4d194e285/result.json) |
| `items.guest-projection` | PASS, 8 | Прежняя регрессия | [result.json](../artifacts/game-tests/e02e7710d05e46979d483c242f510e18/result.json) |
| `world.guest-terrain` | PASS, 4 | Прежняя регрессия | [result.json](../artifacts/game-tests/08343d7fcf9444339621a77835236fd4/result.json) |
| `items.reconnect-state` | PASS, 11 | Пауза транспорта, снимок после возврата, свободный предмет, движение, удаление | [result.json](../artifacts/game-tests/3b111b9e28cc4bb3af9fe1a5b563f6a4/result.json) |
| `world.layout` | PASS, 3 | Прежняя регрессия | [result.json](../artifacts/game-tests/7af57b42e24b4363a702f01acb26ad49/result.json) |
| `world.mob-target` | PASS, 3 | Прежняя регрессия | [result.json](../artifacts/game-tests/99923d263c3d4137aba7a19429a173bb/result.json) |
| `world.mob-target-switch` | PASS, 6 | Конкретный выбор local и переключение на другой Body | [result.json](../artifacts/game-tests/da755e904ad841568cec51075e992571/result.json) |
| `items.native-actor-out-of-reach` | PASS, 6 | Нативная позиция за пределами радиуса, production OutOfReach | [result.json](../artifacts/game-tests/9e2b915c905440938a569cc72a84fdd4/result.json) |
| `items.native-actors-pickup` | PASS, 13 | Два настоящих Body движутся к одному предмету и инициируют гонку через GuestItemSession | [result.json](../artifacts/game-tests/5dc3285344cc4639934f940ddcee919a/result.json) |
| `items.native-pickup` | PASS, 3 | Прежняя регрессия | [result.json](../artifacts/game-tests/f4d58b19690a4c3da43a26a079d240bf/result.json) |
| `debug.pause-step` | PASS, 3 | Пауза исключена из таймаута, один сквозной шаг, возобновление и камера | [result.json](../artifacts/game-tests/ef57af087c61459786637c1beed2044b/result.json) |
| `items.smoke` | PASS, 2 | Прежняя регрессия | [result.json](../artifacts/game-tests/c33daae259364580aab4f961a2d40adb/result.json) |
| `world.terrain-claws` | PASS, 13 | Прежняя регрессия | [result.json](../artifacts/game-tests/7c539a9b16ba4591a469c58b0f655379/result.json) |
| `world.terrain-resync` | PASS, 5 | Два периодических запроса и получение пропущенной клетки после возврата | [result.json](../artifacts/game-tests/565cafd113ac4d77a63c674bd1b505bb/result.json) |

### Границы покрытия

- Переподключение проверено через прерывание виртуального транспорта, события SessionPeers, вход GuestItemSession после возвращения и настоящие кадры снимка. В стенде номер операции из recovery-офера подаётся на вход Core напрямую. Полный `WorldSession.Recovery` с восстановлением локальной сцены и Steam E2E остаются `BLOCKED`.
- Авторитетные предметы хоста и нативные гостевые проекции используют разные экземпляры. `NativeActor` даёт сетевому хосту фактические координаты привязанного Body. Нативный подбор дополнительных Body проверен отдельно от сетевой политики инвентаря.
- Резервуар и перенос предмета водой проверены в одном ванильном Unity-мире. Сетевое применение потока к предметам в независимых мирах и репликация мобов остаются `BLOCKED`.
- Видимый сценарий автоматически вызвал те же переключатели паузы и шага, которые использует окно. Проверено удержание ошибки с заданием 2 секунд. Режим удержания до кнопки End observation реализован, его ручной прогон не проводился.

### Каталог и отрицательные контроли

| Проверка | Итог | Отчёт |
| --- | --- | --- |
| Нативные реестры, targeted probes `alcohol` и `cavetick` | PASS, 300 объявленных предметов, 43 материала, 8 значений жидкости, 3 персонажа. Обе пробы подтвердили prefab. | [result.json](../artifacts/game-tests/1270c5b10b0e4ffc8dec502ecb7d2c95/result.json), [capabilities.json](../artifacts/game-tests/1270c5b10b0e4ffc8dec502ecb7d2c95/capabilities.json) |
| Неверное ожидаемое число подборов | FAIL, ожидаемо. Код 1 и `acceptedCount expected 0, got 1`. | [result.json](../artifacts/game-tests/beb9f9dbb1cf4e63be3ce9fd062d8107/result.json) |
| Недоставленный запрос | FAIL, ожидаемо. Код 1 и `converged expected 1, got 0`. | [result.json](../artifacts/game-tests/ce644e208b3c471695136ea256f40b1f/result.json) |
| Расхождение только по Condition | FAIL, ожидаемо. Код 1 и `replicated expected 1, got 0` после входящего production ItemState с изменённым Condition. | [result.json](../artifacts/game-tests/2c85152a338d45de81063bd770e3043b/result.json) |
| История ландшафта 4097 клеток на протоколе 11 | FAIL. Хост разрушил 4097-ю клетку, гость после возврата подтвердил клетки 1 и 4096, затем истёк срок ожидания состояния клетки 4097. | [result.json](../artifacts/game-tests/4868fe9f3e944c209fea3f6fc5266dfb/result.json) |

Все четыре контроля завершились с точной ожидаемой причиной и кодом 1. Проверка HistoryLimit использовала `TerrainDamageReplication.ApplyHit` и бюджет обычной раздачи, накопление новых клеток после 4096 не сохранялось в истории протокола 11. Код обработки кадров, профилирование и повтор входят в следующий срез синхронизации ландшафта.
