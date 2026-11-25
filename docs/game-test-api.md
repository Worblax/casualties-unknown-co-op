# Game Test API

Команды и запросы регистрируются в `ScenarioCommands` и `ScenarioQueries`. JSON задаёт данные и вызывает операции из этих реестров в Unity main thread. Запись команды задаёт доступные фазы, поля, валидацию и выполнение. Подготовка мира и физиологии отделена от действий, а запросы читают наблюдаемое состояние. Известный отсутствующий binding даёт `BLOCKED`, неизвестное поле или имя — `FAIL`.

| Команды и запросы | Действие |
| --- | --- |
| `item.craft`, `item.craft` с `peer` | Запускает ванильный крафт локально у хоста или через production `SharedItemReplication`/`SharedItemCrafting` выбранного гостя. |
| `items.unownedConverged`, `items.nativeDefinitionCount`, `items.nativeDefinitionInSlots` | Проверка полного совпадения после броска и подсчёт нативных предметов по `id` во всей сцене или только в слотах тела. |
| `items.craftAcceptedCount`, `items.craftRejectedCount`, `items.craftPeerCount` | Число операций с финальным принятым/отклонённым результатом (промежуточный ответ резервирования не считается отдельной операцией) и число гостей, получивших результат. |
| `items.authoritativeDefinitionCount`, `items.replicaDefinitionCount`, `items.definitionConverged` | Подсчёт authoritative/guest-состояний по определению и проверка отсутствия дублей и совпадения реплик. |
| `world.loadSandbox`, `world.loadLayout` | Запуск production Sandbox и установка клеток ASCII-карты через `WorldGeneration.SetBlock`. |
| `peer.add`, `peer.disconnect`, `peer.reconnect` | Участник `VirtualOnly` или `NativeActor`, соединение с хостом и прерывание транспорта. |
| `network.profile` | Заменяет профиль будущих отправок, сохраняя уже запланированную доставку и состояние генератора случайных чисел. |
| `character.spawn`, `character.place`, `character.setState`, `character.speed` | Подготовка тела, позиции, физиологии, выбранной конечности и нативного ограничения скорости. `place` также доступна в шагах для подготовки следующей фазы. |
| `character.move`, `character.crouch`, `character.jump`, `character.ragdoll` | Управление штатным движением и приседанием конкретного Body, вызов стандартных действий прыжка и ragdoll. |
| `item.spawn`, `item.pickup`, `item.drop`, `item.remove` | Создание нативного предмета, сетевые запросы через `GuestItemSession`, удаление Unity-объекта для проверки production-обнаружения исчезновения. |
| `item.nativePickup` | `Body.PickUpItem` локального хоста через production-хук. |
| `item.nativePickupPeer`, `item.nativeDrop`, `item.nativeThrow`, `item.nativeHostDrop`, `item.nativeHostThrow`, `item.nativeWear`, `item.containerLoad` | Нативные действия соответствующего Body через `PickUpItem`, `DropItem`, `ThrowItem`, `WearWearable` и `Container.LoadItem`. |
| `item.alias` | Даёт алиас уже существующему предмету по определению (`id`, `definition`, опционально `peer`); используется для нативных действий с крафт-результатом. |
| `items.guestHoverDescription` | Повторяет физику ванильного `PlayerCamera.HandleWorldHover` и строит настоящее описание через `PlayerCamera.ItemHoverDescription`. В headless-тесте точка запроса — позиция предмета, поскольку `Input.mousePosition` недоступен. |
| `items.auditAuthority`, `items.auditHover`, `items.auditNatives`, `recipe.audit`, `recipe.matchAudit`, `items.auditQualities` | Диагностические отчёты о состоянии реестра, hover, нативных объектах, рецептах и качествах. |
| `character.pickup`, `character.throw` | Нативный инвентарь конкретного Body в изолированном `UNITY_ACTORS` с `standaloneItems: true`. |
| `terrain.hit`, `terrain.breakNative`, `terrain.state` | Сетевой удар в хост, ванильное разрушение для физического теста или входящий кадр гостевой проекции. |
| `fluid.fill`, `fluid.probe`, `fluid.enableFlow` | Заполнение области, объявление пассивной области наблюдения, включение штатных `WaterPusher`. |
| `mob.spawn` | Создаёт проверенный моб. `aggro` и `seeDistance` доступны для `SpiderHandler`. |
| `wait.ready`, `wait.frames`, `wait.physicsTicks`, `parallel` | Готовность сессий, счётчики кадров и Unity FixedUpdate, выполнение совместимых действий до следующего сетевого шага. |
| `snapshot.capture`, `catalog.capture` | Запись промежуточного состояния или каталога ресурсов. |
| `assert.equal`, `assert.eventually`, `assert.always` | Целочисленная, отложенная и непрерывная проверка запроса. |

Видимое окно содержит отдельные Pause scenario и Pause physics, Step, половинную скорость и выбор камеры. При остановленных сценарии и физике Step выполняет один FixedUpdate и один тик сценария. Активный таймаут сценария исключает пользовательскую паузу. `-TimeoutSeconds` у PowerShell остаётся аварийным ограничением до получения результата. Для удержания окна после ошибки доступны `-Visible -HoldOnFailure -HoldOnFailureSeconds N` и `N=0` до кнопки End observation.

`items.converged` сохраняет требование наличия владельца. `items.replicated` также принимает предмет в мире и подтверждённое отсутствие удалённого предмета. Сравниваются ID, определение, владелец, слот, ревизия, номер сэмпла, признак удаления, состояние, координаты, угол, линейная и угловая скорости. Проверка использует состояние реплики после production-декодирования, отдельно от интерполяции Unity. `mob.hasNativeTarget` принимает ID моба в `target` и алиас Body в `actor`. Если `actor` опущен, проверяется `local`.

`items.definitionConverged` сравнивает authoritative-состояния с репликами каждого гостя по идентичности, владельцу, слоту, ревизии, condition и payload. Для пустого набора состояний проверка проходит только при отсутствии предметов у всех участников.

`items.hostNativeCondition` читает condition именно у нативного объекта, связанного с authoritative ID. `items.nativeItemTotal` считает живые `Item.allItems`, а `items.definitionLiquidOrphan` сообщает, что authoritative-состояние потеряло нативный объект. `items.definitionLiquidPositive`/`Biochem` в этом случае выбрасывают ошибку вместо поиска похожего предмета другого участника. Fault point `prepare-result-apply` проверяет очистку уже созданной реплики результата до применения payload.

Уровень `UNITY_HOST` включает настоящий авторитетный мир и независимые гостевые хранилища. В `UNITY_GUEST` только один назначенный гость применяет отдельные production-проекции к Unity. В `UNITY_ACTORS` доступна локальная физика нескольких Body. Поддержка нативного подбора дополнительным Body ограничена локальным сценарием, сетевой подбор отправляет операции через Core. Источник позиции виртуального участника с Body — его живой Unity transform. См. [проверенные bindings](game-test-bindings.md).
