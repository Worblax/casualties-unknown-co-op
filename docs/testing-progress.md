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


## Пространственная репликация мира, протокол 12

Baseline и проверка самостоятельного кодека прошли до миграции. Ванильные `WorldGeneration`, `BlockDamage`, `FluidManager` и `PlayerCamera` исследованы через `ilspycmd -t`. Подтверждены нативный размер 64, полная перерисовка тайлмапа внутри `SetBlock`, особый clamp `SetBlockNoUpdate` и запись жидкости в общий ванильный массив. Периодический проход покрывает прямые записи, которые обходят хуки. Оригинальные игровые сборки служили эталоном.

В последующих прогонах проверены объединённые снимки 8×8, справедливый бюджет, временная перегрузка, восстановление по GUID попытки и Unity-проекция. Отрицательный сценарий протокола 11 перенесён в `tests/scenarios/world/terrain-history-limit.json` и требует совпадения 4097-й клетки после возврата, сохраняя ссылку на старый FAIL как историческое доказательство. Игровой сценарий `fluid-item-flow` выявил дефект наблюдателя, который фиксировал только первый вектор течения. Новая проверка фиксирует положительное изменение скорости от реального `WaterPusher` и последующее смещение в соответствующем направлении. Условия проверки и физика игры сохранились. [Ранний FAIL](../artifacts/game-tests/2c6a473d77a545fba0cd2facc608286e/result.json) · [Повтор 1](../artifacts/game-tests/ec30e6897517441085f0be66b9f88259/result.json) · [Повтор 2](../artifacts/game-tests/112b545ab58440698a6c2f37891675cc/result.json) · [Повтор 3](../artifacts/game-tests/591caa80677c4a3b88f442aec8f5e34f/result.json)

### Нагрузка Core и TestKit

Seed 81. В таблице время моделируемое, объём включает только кадры `WorldChunkSnapshot` хоста. Транспортные заголовки Steam и управляющие кадры сюда не входят. Движущиеся профили также делают 4097 удалений и меняют клеточные слои жидкости. Стоимость чтения, кодирования и применения суммируется по операциям в конкретном прогоне, поэтому её значения зависят от машины и текущей загрузки.

| Сценарий | Первая полная синхронизация | Всего снимков / байт | Чтение / кодирование / применение | Отказы отправки |
| --- | --- | --- | --- | --- |
| 1 гость, неподвижный | 1,45 с | 196 / 10 976 | 1,8 / 0,2 / 0,1 мс | 0 |
| 1 гость, движение и изменения | 1,45 с | 608 / 106 135 | 3,7 / 1,0 / 0,3 мс | 0 |
| 2 гостя, пересечение | 2,65 с | 392 / 21 952 | 1,7 / 0,2 / 0,2 мс | 0 |
| 3 гостя, пересечение | 2,80 с | 602 / 33 712 | 2,7 / 0,3 / 0,4 мс | 0 |
| 3 гостя, разные области, движение | 3,85 с | 1319 / 145 766 | 6,8 / 1,3 / 0,7 мс | 0 |
| 3 гостя, 250 ±100 мс, 4800 Б/с на связь, очередь 18 | 17,65 с | 1166 / 104 467 | 10,3 / 2,3 / 0,7 мс | 1261 |

Первый и последний замеры статических прогонов не различались по байтам снимков в течение последующих 5 секунд. Во время перегрузки после движения, разрушения и изменений жидкости очередь сошлась к нулю на 39,65 с моделируемого времени. Объединение неподтверждённых состояний и сохранение прохода уменьшили объём для трёх разнесённых гостей в нормальном профиле с 170 600 до 145 766 байт. Отдельный ненадёжный поток жидкости по этим измерениям не вводился.

### Матрица инвариантов

- `WorldChunkProtocolChecks` проверяет максимальные 525 байт, даже при неполных краях, кодирование полной воды, пустую клетку, разрежённый урон, неправильные размеры, дубликаты индексов, NaN, повреждённую сумму и чужую сессию.
- `WorldChunkChecks` использует production `HostWorldChunks/GuestWorldChunks` на независимых сетках. Он проверяет первую синхронизацию с отличающимся клиентом, замену, появление, удаление и сброс, редактирование во время доставки снимка, повтор старого кадра, перемещение и повторный вход, гистерезис и прогресс при движении, пересекающиеся и разные подписки, разрыв во время первого снимка, 4097 изменений, отказ применения, повтор ACK, перегрузку и запрос ремонта по локальной контрольной сумме.
- `RecoveryChecks` проверяет существующий протокол возврата предметов, неприменимость старой попытки, повтор и исчерпание срока. Игровой `chunk-reconnect` применяет снимок блоков и воды после разрыва симулированной связи; виртуальный кластер открывает новый поток в точке восстановления. Полный Steam handshake и тайминг нескольких процессов в этой проверке отсутствуют.
- `chunk-reservoir-inside` и `chunk-reservoir-seam` сначала проверяют принадлежность перегородки и сухой клетки к нужным чанкам, затем наблюдают ванильное разрушение, порядок «перегородка перед водой» и сходимость независимых виртуальных сеток. Отдельный `chunk-guest-restore` применяет реальные входящие кадры на Unity-стороне и проверяет материалы, трещины и осушение.
- Сценарии ранней жидкости в отдельном потоке и потери последнего ненадёжного кадра неприменимы. Слои идут в одном полном надёжном снимке. `FluidPush` остаётся временным эффектом. Доказательство физики переноса предмета по жидкости ограничено ванильным сценарием в одном Unity-процессе.

### Итоговый Unity-прогон

Из каталога `mode`:

```powershell
powershell.exe -NoProfile -File ./scripts/Verify.ps1 -DotnetPath 'C:/Program Files/dotnet/dotnet.exe'
powershell.exe -NoProfile -File ./scripts/Verify.ps1 -DotnetPath 'C:/Program Files/dotnet/dotnet.exe' -GameTests -Visible
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/wrong-pickup-count.json -NoBuild -TimeoutSeconds 75
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/undelivered-pickup.json -NoBuild -TimeoutSeconds 75
powershell.exe -NoProfile -File ./scripts/Run-GameTests.ps1 -Scenario tests/scenarios/controls/replica-condition-divergence.json -NoBuild -TimeoutSeconds 75
```

Итоговый `Verify.ps1 -GameTests -Visible` вернул 0. Оба Release-проекта собраны с нулём предупреждений и ошибок. ProtocolChecks, ArchitectureChecks и NetworkChecks прошли. Unity выполнила 26 положительных сценариев и 169 утверждений. Все 26 отчётов `isolation.json` подтвердили сохранность файлов игры и восстановление охваченных PlayerPrefs. Тестовые сборки удалены из каталога плагинов по завершении сессий.

Счётчики ниже относятся к хосту на момент фиксации результата. Многие сценарии специально заканчиваются при сходимости проверяемой области, пока дальняя предзагрузка ещё продолжается. У сценариев гостевой проекции со входящими снимками указан только отчёт, гостевые метрики лежат рядом в `snapshots/final.json`.

| Сценарий PASS | Утверждения | Кадры / байты чанков хоста | Отчёт |
| --- | --- | --- | --- |
| `debug.pause-step` | 3 | 0 / 0 | [result.json](../artifacts/game-tests/f064582b33c849b58f7a21d3740c1b1a/result.json) |
| `items.alcohol-throw` | 7 | 56 / 4270 | [result.json](../artifacts/game-tests/29e55a2f0a334058a969e2f2892b422e/result.json) |
| `items.concurrent-pickup` | 8 | 48 / 3948 | [result.json](../artifacts/game-tests/1ee81cb2e455422d82e60979dc7d99a0/result.json) |
| `items.concurrent-pickup-four-peers` | 8 | 84 / 7350 | [result.json](../artifacts/game-tests/693d2311d1dc45efa21870102d4c2f15/result.json) |
| `items.guest-projection` | 8 | 40 / 3122 | [result.json](../artifacts/game-tests/7ee172e50a4e415abd61a7f0d80d4202/result.json) |
| `items.native-actor-out-of-reach` | 6 | 48 / 3822 | [result.json](../artifacts/game-tests/8f553985223e44a498e5af04201cee3d/result.json) |
| `items.native-actors-pickup` | 13 | 96 / 7644 | [result.json](../artifacts/game-tests/2222e26c587a45788501906a5ad904c1/result.json) |
| `items.native-pickup` | 3 | 24 / 1974 | [result.json](../artifacts/game-tests/e1edfc7d65944a96a207631bbaaeabdd/result.json) |
| `items.reconnect-state` | 11 | 80 / 6622 | [result.json](../artifacts/game-tests/ec4476848a604224813b4a5e345ec6c9/result.json) |
| `items.smoke` | 2 | 0 / 0 | [result.json](../artifacts/game-tests/9d1ad00d818b4590985fc3d6de2a5ce5/result.json) |
| `world.actor-native-actions` | 13 | 0 / 0 | [result.json](../artifacts/game-tests/71da51e54e534aaf8caaff7f39b54801/result.json) |
| `world.body-ragdoll` | 4 | 0 / 0 | [result.json](../artifacts/game-tests/0c101b02e2ac46298f91032b93624a1a/result.json) |
| `world.chunk-guest-restore` | 8 | — | [result.json](../artifacts/game-tests/4f471e64d90542f29457e6875dc18d25/result.json) |
| `world.chunk-reconnect` | 6 | 48 / 3971 | [result.json](../artifacts/game-tests/5b0a1e81337b4d5599e9542fff06c40b/result.json) |
| `world.chunk-reservoir-inside` | 9 | 403 / 32362 | [result.json](../artifacts/game-tests/896d3edd40264f078a8c5bb6501c8f80/result.json) |
| `world.chunk-reservoir-seam` | 9 | 403 / 32362 | [result.json](../artifacts/game-tests/cd45ec0b519647a883994a7e77606c97/result.json) |
| `world.fluid-cell` | 3 | 0 / 0 | [result.json](../artifacts/game-tests/68eaa4295b6b4807951c61d57e3d4a38/result.json) |
| `world.fluid-item-flow` | 4 | 0 / 0 | [result.json](../artifacts/game-tests/1a11859ce90c4b34a89c51593a87ef20/result.json) |
| `world.fluid-reservoir` | 5 | 0 / 0 | [result.json](../artifacts/game-tests/e0a20b2d64d54087a81130f338e6f304/result.json) |
| `world.guest-terrain` | 4 | — | [result.json](../artifacts/game-tests/f7665d7ced8e4aadbf464bd8a9ecb817/result.json) |
| `world.layout` | 3 | 0 / 0 | [result.json](../artifacts/game-tests/9f7770d764e6456597b27cf7515e8680/result.json) |
| `world.mob-target` | 3 | 0 / 0 | [result.json](../artifacts/game-tests/1c793e2d9bff44a3ae3ef6c7004db976/result.json) |
| `world.mob-target-switch` | 6 | 0 / 0 | [result.json](../artifacts/game-tests/86201639f8b2404389a0bea6e2cc20d3/result.json) |
| `world.terrain-claws` | 13 | 80 / 7020 | [result.json](../artifacts/game-tests/bb530e686f8f40d3a47857919188ee36/result.json) |
| `world.terrain-history-limit` | 5 | 224 / 24388 | [result.json](../artifacts/game-tests/ba932040567d43b3a897dccbcb9fe4cf/result.json) |
| `world.terrain-resync` | 5 | 56 / 5162 | [result.json](../artifacts/game-tests/de38a0a42f9c4c41be38996119b32f50/result.json) |

Три отрицательных контроля вернули код 1 и свои ожидаемые ошибки. Сценарий с неверным числом подборов увидел один подбор, сценарий с прерванной доставкой не дождался сходимости, сценарий с искажённым Condition обнаружил различие реплики. [Неверное ожидаемое число](../artifacts/game-tests/4267894012844ac08d19112dff90e5c7/result.json) · [Недоставленный запрос](../artifacts/game-tests/c3d2584e07f04f6b9d87f5dc291a7c42/result.json) · [Расхождение Condition](../artifacts/game-tests/3f96701eaaf44e23993ffaeda7ffc5b8/result.json)

Steam E2E, независимые Unity-миры и сетевой перенос общего предмета течением между ними не проходили. В видимой сборке доступен выбор области, ручная проверка переключателя Show chunks не проводилась.
