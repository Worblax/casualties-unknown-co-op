# Формат сценариев v1

JSON UTF-8. Обязательные поля — `schemaVersion: 1`, `name`, `level`, `seed`, `timeoutMs`, `network`, `setup`, `steps`. Поддерживаются существующие сценарии v1. Размер файла до 64 КБ, до 48 записей в каждой последовательности, таймаут сценария от 500 до 180000 мс. Первой командой setup идёт `world.loadSandbox`. За ней объявляется один хост и до трёх гостей. Позиции — пары чисел, в единицах Unity относительно начальной точки Sandbox. У предметов сохраняется смещение вверх на 0,8 из прежних сценариев.

Пример привязки гостя к персонажу:

```json
{
  "setup": [
    { "op": "world.loadSandbox" },
    { "op": "peer.add", "id": "host", "role": "host" },
    { "op": "character.spawn", "id": "runner", "character": 0, "position": [-6, 0] },
    { "op": "peer.add", "id": "alice", "role": "guest", "mode": "NativeActor", "body": "runner" }
  ],
  "steps": [
    { "op": "wait.ready" },
    { "op": "character.move", "target": "runner", "axis": 1 },
    { "op": "assert.eventually", "timeoutMs": 3000,
      "condition": { "op": "character.moved", "target": "runner", "distance": 2 } }
  ]
}
```

Хост всегда связан с `local`. Гости без `mode` используют `VirtualOnly` и координаты из `peer.add.position`. Каждый `NativeActor` должен ссылаться на отдельный Body через `body`. Для виртуального участника поле `body` недопустимо. `limits` содержит `items` (по умолчанию 16, максимум 64), `mobs` (6, максимум 16) и `bodies` (4, максимум 8, включая локальное тело). `standaloneItems: true` включает нативные предметы для `UNITY_ACTORS` с одним хостом.

`network` и `network.profile.profile` используют миллисекунды для `latencyMs`, `jitterMs` и `unreliableReorderMs`, доли 0..1 для `unreliableLoss` и `unreliableDuplication`, целевую скорость для `bytesPerSecond`. `parallel.actions` планирует операции по порядку в одном тике до доставки кадров. Ожидания, проверки, setup и вложенный `parallel` в группу не входят. Конфликт двух запросов одного гостя предметная сессия отклоняет по правилу одной незавершённой операции.

`wait.frames` и `wait.physicsTicks` принимают `count` от 1 до 1000. `assert.eventually` и `assert.always` принимают `condition` с `op`, необязательным `expected` (по умолчанию 1) и аргументами запроса. `assert.equal` требует `query` и `expected`. В запросах `actor` — алиас персонажа, `target` — алиас области, моба или персонажа, `item` — алиас предмета. `snapshot.capture.id` должен быть уникален, до 32 символов, допустимы буквы, цифры, `-` и `_`. Имя `final` зарезервировано.

`Run-GameTests.ps1 -Scenario <путь>` запускает один сценарий. `-All` пропускает `controls`, `blocked` и, без `-Visible`, `visible`. `-Catalog` запускает только сбор каталога. `-ProbeItem` и `-ProbeMob` принимают до 24 дополнительных путей суммарно. Криптографические хеши, итог и результаты проверок находятся в `artifacts/game-tests/<runId>/result.json`. Рядом сохраняются `scenario.json`, `network.jsonl`, `capabilities.json`, `snapshots/*.json`, `isolation.json` и логи. Снимки читают авторитетный реестр, независимые реплики и реальные Unity-объекты. Прогоны со статусом FAIL или BLOCKED возвращают ненулевой код. Контроли запускаются явно, чтобы проверка их ожидаемых отказов не скрывала причины ошибок обычных сценариев.

Полные примеры — [гонка Body](../tests/scenarios/items/native-actors-pickup.json), [состояние после возврата](../tests/scenarios/items/item-reconnect-state.json), [смена цели моба](../tests/scenarios/world/mob-target-switch.json), [пауза и камера](../tests/scenarios/visible/pause-step.json).
