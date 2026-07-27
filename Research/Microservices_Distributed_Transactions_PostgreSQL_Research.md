# Исследование: Распределённые транзакции между микросервисами с PostgreSQL

## Оглавление
1. [Проблема распределённых транзакций](#1-проблема-распределённых-транзакций)
2. [ACID в монолите vs микросервисах](#2-acid-в-монолите-vs-микросервисах)
3. [Паттерн 2PC (Two-Phase Commit)](#3-паттерн-2pc-two-phase-commit)
4. [Паттерн Saga (Choreography и Orchestration)](#4-паттерн-saga-choreography-и-orchestration)
5. [Паттерн Outbox (Transactional Outbox)](#5-паттерн-outbox-transactional-outbox)
6. [Паттерн CQRS и Event Sourcing](#6-паттерн-cqrs-и-event-sourcing)
7. [Паттерн TCC (Try-Confirm-Cancel)](#7-паттерн-tcc-try-confirm-cancel)
8. [Идемпотентность и компенсирующие транзакции](#8-идемпотентность-и-компенсирующие-транзакции)
9. [PostgreSQL-специфичные механизмы](#9-postgresql-специфичные-механизмы)
10. [Сравнение паттернов](#10-сравнение-паттернов)
11. [Рекомендуемая архитектура](#11-рекомендуемая-архитектура)
12. [Практический пример: e-commerce заказ](#12-практический-пример-e-commerce-заказ)
13. [Антипаттерны](#13-антипаттерны)
14. [Глоссарий](#14-глоссарий)

---

## 1. Проблема распределённых транзакций

В микросервисной архитектуре каждый сервис имеет **собственную базу данных** (Database-per-service).
Это фундаментальный принцип: сервисы слабо связаны, независимо деплоятся и масштабируются.

Когда бизнес-операция затрагивает **несколько сервисов** (например, оформление заказа:
`Order Service` → `Payment Service` → `Inventory Service` → `Shipping Service`), возникает
проблема — как обеспечить целостность данных, если:

- Каждая БД изолирована
- Нет единого координатора транзакций
- Сеть ненадёжна (сбои, таймауты, дубликаты)
- Сервисы могут быть временно недоступны

### Ключевой вопрос

> Как гарантировать, что операция, затрагивающая несколько независимых БД, либо полностью
> выполнится, либо полностью откатится — без блокировок, которые «замораживают» всю систему?

---

## 2. ACID в монолите vs микросервисах

### Монолит: классический ACID

```
┌─────────────────────────────────────┐
│         Монолитное приложение        │
│  ┌───────────────────────────────┐  │
│  │      BEGIN TRANSACTION        │  │
│  │  INSERT INTO orders ...       │  │
│  │  UPDATE accounts SET balance  │  │
│  │  UPDATE inventory SET qty     │  │
│  │      COMMIT / ROLLBACK        │  │
│  └───────────────────────────────┘  │
│         Одна база данных            │
└─────────────────────────────────────┘
```

| Свойство | Монолит (1 БД) | Микросервисы (N БД) |
|----------|----------------|---------------------|
| **A — Atomicity** | ✅ Легко (одна транзакция) | ❌ Требует распределённого протокола |
| **C — Consistency** | ✅ Ограничения БД | ⚠️ Внешние ограничения, итоговая согласованность |
| **I — Isolation** | ✅ Уровни изоляции БД | ❌ Нет общего менеджера блокировок |
| **D — Durability** | ✅ WAL / журналирование | ⚠️ Каждая БД отдельно |

### Переход к BASE

Микросервисы переходят от **ACID** к **BASE**:

- **B**asically **A**vailable — система остаётся доступной
- **S**oft state — состояние может меняться без ввода (репликация, отложенные обновления)
- **E**ventual consistency — согласованность достигается **со временем**, не мгновенно

> Ключевой сдвиг: от мгновенной целостности — к **итоговой согласованности** (eventual consistency)
> через компенсирующие операции и события.

---

## 3. Паттерн 2PC (Two-Phase Commit)

### Принцип

2PC — классический протокол распределённых транзакций с **координатором**.

```
                ┌──────────────┐
                │  Координатор  │
                └──────┬───────┘
                       │
         ┌─────────────┼─────────────┐
         │             │             │
    ┌────▼────┐   ┌────▼────┐   ┌────▼────┐
    │  БД-1   │   │  БД-2   │   │  БД-3   │
    │PostgreSQL│   │PostgreSQL│   │PostgreSQL│
    └─────────┘   └─────────┘   └─────────┘
```

### Фаза 1: Prepare (подготовка)

1. Координатор отправляет `PREPARE` всем участникам
2. Каждый участник проверяет возможность коммита, ставит блокировки, пишет в WAL
3. Участник отвечает `VOTE_COMMIT` или `VOTE_ABORT`
4. Если **все** ответили `VOTE_COMMIT` → переход к Фазе 2

### Фаза 2: Commit / Rollback

- Если все готовы → координатор шлёт `COMMIT` всем
- Если хоть один `VOTE_ABORT` (или таймаут) → координатор шлёт `ROLLBACK` всем
- Участники подтверждают выполнение

### PostgreSQL и 2PC

PostgreSQL поддерживает 2PC на уровне протокола:

```sql
-- Подготовка распределённой транзакции
PREPARE TRANSACTION 'txn_order_12345';

-- Подтверждение (коммит)
COMMIT PREPARED 'txn_order_12345';

-- Откат
ROLLBACK PREPARED 'txn_order_12345';
```

Особенности PostgreSQL:
- Подготовленные транзакции сохраняются в WAL и **переживают перезапуск** сервера
- Блокировки удерживаются до `COMMIT PREPARED` / `ROLLBACK PREPARED`
- Параметр `max_prepared_transactions` (по умолчанию 0 — нужно включить)
- Подготовленные транзакции накапливаются, если координатор «упал» — нужен мониторинг
  (`pg_prepared_xacts`)

### Плюсы

- ✅ Строгая ACID-гарантия атомарности
- ✅ Встроенная поддержка в PostgreSQL
- ✅ Простая семантика — всё или ничего

### Минусы

- ❌ **Блокирующий протокол** — участники держат блокировки во время обеих фаз
- ❌ **SPOF координатора** — если координатор упал, участники «зависают»
- ❌ **Медленный** — минимум 2 сетевых раунда + запись WAL на каждом узле
- ❌ **Не масштабируется** — падает производительность с ростом числа участников
- ❌ **Хрупкость при сбоях сети** — таймауты оставляют подготовленные транзакции
- ❌ **Противоречит микросервисной архитектуре** — требует общей транзакционной логики

> ⚠️ **Вердикт:** 2PC подходит для tightly-coupled систем (например, шардирование одной логической БД),
> но **не рекомендуется для микросервисов** из-за блокировок и слабой устойчивости к сбоям.

---

## 4. Паттерн Saga (Choreography и Orchestration)

### Принцип

Saga — последовательность **локальных транзакций**, где каждая:
1. Обновляет данные **одного** сервиса
2. Публикует **событие** (message/event), запускающее следующую транзакцию
3. При сбое — выполняется **компенсирующая транзакция** (откат предыдущих шагов)

> Saga **не изолирует** параллельные выполнения и не обеспечивает мгновенный откат.
> Вместо этого — компенсирующие операции для логического «отката».

### Вариант A: Choreography (хореография)

Нет центрального координатора — сервисы реагируют на события друг друга.

```
Order Service     Payment Service     Inventory Service     Shipping Service
     │                   │                     │                    │
     │── OrderCreated ──►│                     │                    │
     │                   │── PaymentBilled ───►│                    │
     │                   │                     │── InventoryReserved│──►
     │                   │                     │                    │
     │                   │                     │  [Сбой на Shipping] │
     │                   │                     │◄── ReleaseInventory │
     │                   │◄── RefundPayment ───│                    │
     │◄── OrderRejected ─│                     │                    │
```

**Плюсы:** простота, нет SPOF, слабая связанность
**Минусы:** сложна для отладки, циклические зависимости, сложно отследить整个 поток

### Вариант B: Orchestration (оркестрация)

Центральный **оркестратор** (Saga Orchestrator) управляет последовательностью.

```
                    ┌──────────────────┐
                    │  Saga             │
                    │  Orchestrator     │
                    └────────┬──────────┘
                             │
            ┌────────────────┼────────────────┐
            │                │                │
     ┌──────▼──────┐  ┌──────▼──────┐  ┌──────▼──────┐
     │    Order    │  │   Payment   │  │  Inventory  │
     │   Service   │  │   Service   │  │   Service   │
     └─────────────┘  └─────────────┘  └─────────────┘
```

Шаги оркестратора:
1. `Create Order` → Order Service
2. `Process Payment` → Payment Service
3. `Reserve Inventory` → Inventory Service
4. `Schedule Shipping` → Shipping Service

При сбое на шаге N оркестратор запускает **компенсации** в обратном порядке:
- `Cancel Shipping` → `Release Inventory` → `Refund Payment` → `Reject Order`

**Плюсы:** ясная логика, централизованная обработка ошибок, проще отладка
**Минусы:** оркестратор — SPOF (нужна репликация), больше связанность с сервисами

### Компенсирующие транзакции

| Операция | Компенсация |
|----------|-------------|
| `CreateOrder` | `RejectOrder` (отметить как отменённый) |
| `ProcessPayment` | `RefundPayment` (возврат средств) |
| `ReserveInventory` | `ReleaseInventory` (освободить товар) |
| `ShipOrder` | `CancelShipment` (отменить доставку) |

> Важно: компенсация — **не** технический `ROLLBACK`. Это **бизнес-операция**:
> нельзя «удалить» платёж — нужно создать возврат. Нельзя «удалить» отгрузку — нужно её отменить.

### Реализация состояния Saga

Состояние saga хранится в таблице PostgreSQL:

```sql
CREATE TABLE saga_instances (
    saga_id          UUID PRIMARY KEY,
    saga_type        VARCHAR(100) NOT NULL,    -- 'order_creation'
    current_step     INT NOT NULL DEFAULT 0,
    status           VARCHAR(20) NOT NULL,     -- RUNNING, COMPLETED, COMPENSATING, FAILED
    payload          JSONB NOT NULL,           -- данные бизнес-операции
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE saga_steps (
    id               SERIAL PRIMARY KEY,
    saga_id          UUID REFERENCES saga_instances(saga_id),
    step_number      INT NOT NULL,
    step_name        VARCHAR(100) NOT NULL,
    status           VARCHAR(20) NOT NULL,     -- PENDING, COMPLETED, FAILED, COMPENSATED
    request_data     JSONB,
    response_data    JSONB,
    executed_at      TIMESTAMPTZ,
    INDEX idx_saga_steps_saga (saga_id, step_number)
);
```

### Плюсы Saga

- ✅ Неблокирующие локальные транзакции
- ✅ Хорошо масштабируется
- ✅ Устойчива к сбоям (компенсации)
- ✅ Естественно ложится на микросервисы

### Минусы Saga

- ❌ **Нет изоляции** между шагами — параллельные saga могут видеть промежуточные состояния
- ❌ Сложность отладки (особенно choreography)
- ❌ Компенсирующие операции — бизнес-логика, не всегда тривиальны
- ❌ Итоговая согласованность, не мгновенная

---

## 5. Паттерн Outbox (Transactional Outbox)

### Проблема

Надёжная доставка событий — классическая дилемма:

```
Вариант 1: Сначала БД, потом сообщение
  ┌──────────┐     ┌──────────┐
  │  COMMIT  │ ──► │ Publish  │   ← Если publish упал → сообщение потеряно
  └──────────┘     └──────────┘

Вариант 2: Сначала сообщение, потом БД
  ┌──────────┐     ┌──────────┐
  │ Publish  │ ──► │  COMMIT  │   ← Если commit упал → сообщение отправлено зря
  └──────────┘     └──────────┘
```

Невозможно атомарно сделать коммит в БД и отправить сообщение в брокер — это две разные системы.

### Решение: Outbox Pattern

Записать событие в **ту же транзакцию**, что и бизнес-данные, в отдельную таблицу `outbox`.
Затем **отдельный процесс** (Outbox Publisher) читает таблицу и отправляет в брокер.

```
    Service (одна PostgreSQL-транзакция)
    ┌─────────────────────────────────────┐
    │  BEGIN;                              │
    │  INSERT INTO orders ...;             │  ← бизнес-данные
    │  INSERT INTO outbox (event_data);    │  ← событие
    │  COMMIT;                             │  ← атомарно!
    └─────────────────────────────────────┘
                    │
                    ▼
    ┌──────────────────────────┐
    │    Outbox Publisher       │
    │  (polling или CDC)        │
    │  SELECT * FROM outbox     │
    │  WHERE sent = false;      │
    │  → publish to Kafka/RabbitMQ │
    │  → UPDATE outbox SET sent │
    └──────────────────────────┘
```

### Схема таблицы outbox в PostgreSQL

```sql
CREATE TABLE outbox (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_id    UUID NOT NULL,           -- ID бизнес-сущности
    aggregate_type  VARCHAR(50) NOT NULL,    -- 'Order', 'Payment'
    event_type      VARCHAR(100) NOT NULL,   -- 'OrderCreated', 'PaymentCompleted'
    payload         JSONB NOT NULL,          -- данные события
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at    TIMESTAMPTZ,             -- NULL = не отправлено
    retry_count     INT NOT NULL DEFAULT 0,
    version         BIGINT NOT NULL          -- для ordering
);

-- Индекс для быстрого поиска неотправленных событий
CREATE INDEX idx_outbox_unprocessed
    ON outbox (created_at)
    WHERE processed_at IS NULL;
```

### Два способа публикации

#### Способ 1: Polling (опрашивание)

```sql
-- Publisher периодически выполняет:
SELECT * FROM outbox
WHERE processed_at IS NULL
ORDER BY created_at
LIMIT 100
FOR UPDATE SKIP LOCKED;   -- ключевой момент! (см. ниже)
```

`FOR UPDATE SKIP LOCKED` — PostgreSQL-специфичная фича: пропускает строки,
уже заблокированные другими воркерами. Позволяет нескольким publisher-процессам
работать параллельно без конфликтов.

#### Способ 2: CDC (Change Data Capture) через Logical Decoding

PostgreSQL поддерживает **logical replication** — можно создать logical decoding plugin
и читать WAL напрямую:

```
PostgreSQL WAL → Logical Decoding Plugin → Debezium → Kafka
```

**Debezium** — популярный инструмент CDC для PostgreSQL:
- Читает изменения из WAL через `pgoutput` plugin
- Преобразует в события и публикует в Kafka
- At-least-once доставка, гарантированный порядок
- Не требует polling-таблицы outbox (но outbox всё равно полезен для структурирования)

**Преимущества CDC над polling:**
- Низкая задержка (миллисекунды вместо секунд)
- Нет нагрузки от polling-запросов на БД
- Гарантированный порядок событий
- Не нужно следить за `processed_at`

**Настройка PostgreSQL для CDC:**

```ini
# postgresql.conf
wal_level = logical
max_replication_slots = 10
max_wal_senders = 10
```

```sql
-- Создание публикации и слота репликации
CREATE PUBLICATION db_outbox FOR TABLE outbox;
SELECT * FROM pg_create_logical_replication_slot('outbox_slot', 'pgoutput');
```

### Идемпотентность при Outbox

Поскольку доставка **at-least-once** (не exactly-once), потребитель должен быть идемпотентным:

- Каждое событие имеет уникальный `id` (UUID)
- Получатель проверяет: `SELECT * FROM processed_events WHERE event_id = ?`
- Если уже обработано — игнорирует
- Если нет — обрабатывает и записывает в `processed_events` (в той же транзакции, что и бизнес-данные)

```sql
-- На стороне потребителя:
BEGIN;
INSERT INTO processed_events (event_id, processed_at) VALUES (?, NOW());
-- + бизнес-логика
COMMIT;
```

### Плюсы Outbox

- ✅ **Атомарность** бизнес-данных и событий (одна транзакция)
- ✅ Гарантированная доставка (at-least-once)
- ✅ Не теряет события при сбое сервиса
- ✅ Естественная интеграция с PostgreSQL
- ✅ `FOR UPDATE SKIP LOCKED` для параллельной обработки

### Минусы Outbox

- ❌ Требует дополнительной таблицы и процесса-публикатора
- ❌ At-least-once (нужна идемпотентность на потребителе)
- ❌ Polling-вариант создаёт нагрузку на БД
- ❌ CDC-вариант сложнее в настройке (logical replication, Debezium)

---

## 6. Паттерн CQRS и Event Sourcing

### CQRS (Command Query Responsibility Segregation)

Разделение операций:
- **Commands** (запись) — изменяют состояние, не возвращают данные
- **Queries** (чтение) — возвращают данные, не изменяют состояние

```
         ┌─────────────┐         ┌──────────────┐
  Command│  Command    │         │   Query      │Query
  ──────►│  Handler    │         │   Handler    │◄──────
         │  (Write DB) │         │   (Read DB)  │
         └──────┬──────┘         └──────▲───────┘
                │                       │
                │   Event / Sync        │
                └───────────────────────┘
```

### Event Sourcing

Вместо хранения текущего состояния — хранится **история событий**.
Текущее состояние вычисляется **проекцией** (replay) событий.

```sql
-- Таблица событий (event store)
CREATE TABLE event_store (
    event_id         UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_id     UUID NOT NULL,
    aggregate_type   VARCHAR(50) NOT NULL,
    event_type       VARCHAR(100) NOT NULL,
    payload          JSONB NOT NULL,
    metadata         JSONB,
    version          BIGINT NOT NULL,        -- версия агрегата
    created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (aggregate_id, version)           -- оптимистичная блокировка
);

CREATE INDEX idx_event_store_agg
    ON event_store (aggregate_id, version);
```

Пример последовательности событий для заказа:

| version | event_type | payload |
|---------|------------|---------|
| 1 | `OrderCreated` | `{orderId, customerId, items}` |
| 2 | `PaymentCompleted` | `{orderId, amount, txnId}` |
| 3 | `InventoryReserved` | `{orderId, items}` |
| 4 | `OrderShipped` | `{orderId, trackingId}` |

Текущее состояние заказа = replay всех событий → `OrderStatus = SHIPPED`.

### Проекции для Query-стороны

События проецируются в **read-optimized** таблицы:

```sql
CREATE TABLE order_view (
    order_id         UUID PRIMARY KEY,
    customer_id      UUID NOT NULL,
    status           VARCHAR(20) NOT NULL,
    total_amount     DECIMAL(10,2),
    payment_txn_id   UUID,
    tracking_id      VARCHAR(50),
    created_at       TIMESTAMPTZ,
    updated_at       TIMESTAMPTZ
);

-- Проекция обновляется при обработке каждого события
-- (через подписку на event store / outbox)
```

### Связь CQRS + Event Sourcing с распределёнными транзакциями

- **Event Sourcing** заменяет традиционные UPDATE/DELETE — только INSERT событий
- События — это **source of truth**, а таблицы-проекции — оптимизация для чтения
- Распределённая согласованность достигается через **подписку на события**
  других сервисов (event-driven)
- Каждый сервис хранит свои события, обменивается через брокер (Kafka, RabbitMQ)

### Плюсы

- ✅ Полный аудит (каждое изменение — событие)
- ✅ Возможность «перемотки» состояния на любой момент времени (time travel)
- ✅ Read и write модели оптимизированы независимо
- ✅ Естественная интеграция с Outbox

### Минусы

- ❌ Значительная сложность реализации
- ❌ Eventual consistency между write и read моделями
- ❌ Рост объёма event store (нужна snapshotting-стратегия)
- ❌ Сложно重构ить/мигрировать схему событий

---

## 7. Паттерн TCC (Try-Confirm-Cancel)

### Принцип

TCC — двухфазный протокол на **бизнес-уровне** (не на уровне БД, как 2PC).

| Фаза | Действие | Пример (резервирование $100) |
|------|----------|-------------------------------|
| **Try** | Зарезервировать ресурсы (не списывать) | Заморозить $100 на счёте |
| **Confirm** | Подтвердить операцию | Списать $100, убрать резерв |
| **Cancel** | Отменить резерв | Разморозить $100 |

```
         ┌──────────────┐
         │  Координатор  │
         └──────┬───────┘
                │
    ┌───────────┼───────────┐
    │           │           │
  Try──►  Try──►    Try──►
    │           │           │
  Все OK? ──► Confirm всем
  Сбой?   ──► Cancel всем
```

### Реализация в PostgreSQL

```sql
-- Таблица TCC-транзакций
CREATE TABLE tcc_transactions (
    txn_id           UUID PRIMARY KEY,
    participant      VARCHAR(50) NOT NULL,    -- 'payment', 'inventory'
    status           VARCHAR(20) NOT NULL,    -- TRYING, CONFIRMED, CANCELLED
    business_data    JSONB NOT NULL,
    created_at       TIMESTAMPTZ DEFAULT NOW(),
    expires_at       TIMESTAMPTZ NOT NULL     -- таймаут для автоматического cancel
);

-- Пример: Try — резервирование средств
BEGIN;
INSERT INTO accounts (id, reserved_amount)
VALUES (..., reserved_amount + 100)
WHERE available_amount >= 100;
INSERT INTO tcc_transactions VALUES (...,'TRYING',...);
COMMIT;
```

### Плюсы

- ✅ Неблокирующий (ресурсы резервируются, не держат БД-блокировки)
- ✅ Гарантированный confirm/cancel (в отличие от 2PC)
- ✅ Подходит для строгих бизнес-требований (платежи, бронирования)

### Минусы

- ❌ Каждый сервис должен реализовать 3 операции (Try, Confirm, Cancel) — сложность
- ❌ Нужно хранить состояние TCC-транзакций
- ❌ Try может не получиться (нехватка ресурсов) — нужен обработчик
- ❌ Таймауты и автоматический cancel требуют фонового процесса

---

## 8. Идемпотентность и компенсирующие транзакции

### Идемпотентность — основа надёжности

При распределённых операциях сообщения **могут доставляться дважды** (at-least-once).
Каждая операция должна быть **идемпотентной** — повторное выполнение даёт тот же результат.

### Стратегии идемпотентности в PostgreSQL

#### Стратегия 1: Unique Constraint + Upsert

```sql
-- Каждая операция имеет уникальный idempotency_key
CREATE TABLE payments (
    id               UUID PRIMARY KEY,
    idempotency_key  VARCHAR(100) UNIQUE NOT NULL,  -- ключ идемпотентности
    order_id         UUID NOT NULL,
    amount           DECIMAL(10,2) NOT NULL,
    status           VARCHAR(20) NOT NULL,
    created_at       TIMESTAMPTZ DEFAULT NOW()
);

-- INSERT ... ON CONFLICT — PostgreSQL upsert
INSERT INTO payments (id, idempotency_key, order_id, amount, status)
VALUES (?, ?, ?, ?, 'COMPLETED')
ON CONFLICT (idempotency_key) DO NOTHING
RETURNING *;
```

#### Стратегия 2: Таблица идемпотентности

```sql
CREATE TABLE processed_requests (
    idempotency_key  VARCHAR(100) PRIMARY KEY,
    request_hash     VARCHAR(64) NOT NULL,    -- хэш тела запроса для проверки
    response_data    JSONB,                    -- кэшированный ответ
    processed_at     TIMESTAMPTZ DEFAULT NOW()
);

-- Перед выполнением:
BEGIN;
INSERT INTO processed_requests (idempotency_key, request_hash)
VALUES (?, ?)
ON CONFLICT DO NOTHING
RETURNING idempotency_key;

-- Если вернул ключ → операция новая → выполняем
-- Если ничего не вернул → уже выполнялась → возвращаем кэшированный ответ
COMMIT;
```

#### Стратегия 3: Версионирование (оптимистичная блокировка)

```sql
-- Каждое обновление проверяет версию
UPDATE accounts
SET balance = balance - 100, version = version + 1
WHERE id = ? AND version = ?;
-- Если затронуто 0 строк → кто-то изменил ранее → конфликт
```

### Компенсирующие транзакции: принципы

1. **Не удаляют данные** — создают компенсирующие записи
2. **Идемпотентны** — безопасны при повторе
3. **Ассоциативны** — порядок компенсаций = обратный порядок операций
4. **Сохраняют аудит** — компенсация видна в истории

Пример:

```sql
-- Вместо DELETE FROM payments WHERE order_id = ?
-- Делаем:
INSERT INTO payments (id, order_id, amount, type, status)
VALUES (?, ?, 100, 'REFUND', 'COMPLETED');

UPDATE orders SET status = 'CANCELLED' WHERE id = ?;
```

---

## 9. PostgreSQL-специфичные механизмы

### 9.1. PREPARE TRANSACTION (двухфазная фиксация)

```sql
-- Включить поддержку
-- postgresql.conf: max_prepared_transactions = 100

BEGIN;
INSERT INTO orders (...) VALUES (...);
PREPARE TRANSACTION 'order_txn_001';
-- Транзакция теперь "подвешена" и переживает перезапуск

-- Позже (координатор):
COMMIT PREPARED 'order_txn_001';
-- или
ROLLBACK PREPARED 'order_txn_001';
```

Мониторинг:
```sql
SELECT * FROM pg_prepared_xacts;
-- gid, prepared, owner, database
```

> ⚠️ Подготовленные транзакции удерживают блокировки и не очищаются автоматически
> при падении координатора. Нужен механизм восстановления.

### 9.2. postgres_fdw (Foreign Data Wrapper)

`postgres_fdw` позволяет обращаться к таблицам **другого PostgreSQL-сервера** как к локальным.

```sql
-- Создание внешнего сервера
CREATE SERVER remote_payment
    FOREIGN DATA WRAPPER postgres_fdw
    OPTIONS (host 'payment-db', dbname 'payments', port '5432');

-- Создание user mapping
CREATE USER MAPPING FOR app_user
    SERVER remote_payment
    OPTIONS (user 'remote_user', password '***');

-- Импорт схемы
IMPORT FOREIGN SCHEMA public
    FROM SERVER remote_payment
    INTO remote_schema;

-- Теперь можно делать распределённые запросы:
SELECT o.id, o.total, p.amount
FROM orders o
JOIN remote_schema.payments p ON p.order_id = o.id;
```

**FDW и 2PC:** postgres_fdw поддерживает двухфазные транзакции при записи
в несколько foreign servers (с `max_prepared_transactions > 0`).

> ⚠️ Это фактически связывает микросервисы на уровне БД, что **нарушает** изоляцию.
> Рекомендуется только для аналитических запросов (read), не для OLTP-транзакций.

### 9.3. LISTEN/NOTIFY (асинхронные уведомления)

Встроенный механизм pub/sub на уровне PostgreSQL:

```sql
-- Сервис A: в транзакции
BEGIN;
INSERT INTO orders (...) VALUES (...);
NOTIFY order_events, '{"event":"OrderCreated","orderId":"123"}';
COMMIT;
-- Уведомление отправляется только при COMMIT

-- Сервис B: слушает
LISTEN order_events;
-- Получает уведомление в приложении
```

**Плюсы:** просто, встроено, работает в рамках одной БД
**Минусы:** уведомления не сохраняются (если слушатель офлайн — пропустит),
не подходит для межсервисного взаимодействия (разные БД), объём payload ограничен.

> Используется как лёгкая альтернатива Outbox-polling **внутри одного сервиса**:
> publisher пишет в outbox и `NOTIFY`, воркер просыпается и читает outbox.

### 9.4. Logical Decoding / WAL (для CDC)

```sql
-- Включить
-- postgresql.conf: wal_level = logical

-- Создать slot
SELECT * FROM pg_create_logical_replication_slot(
    'outbox_slot', 'pgoutput');

-- Создать publication
CREATE PUBLICATION outbox_pub FOR TABLE outbox;
```

Инструменты: **Debezium**, **pglogical**, **Bucardo**, **pgvector** (для векторов).

### 9.5. FOR UPDATE SKIP LOCKED (параллельная обработка очередей)

```sql
-- Несколько воркеров могут одновременно забирать задачи из очереди
-- без конфликтов:
SELECT * FROM outbox
WHERE processed_at IS NULL
ORDER BY created_at
LIMIT 100
FOR UPDATE SKIP LOCKED;

-- Воркер 1 забирает строки 1-100
-- Воркер 2 забирает строки 101-200 (пропускает заблокированные)
```

### 9.6. Advisory Locks (блокировки на уровне приложения)

```sql
-- Блокировка по ключу (например, ID заказа) для сериализации операций
SELECT pg_advisory_lock(12345);    -- блокировка
-- ... критическая секция ...
SELECT pg_advisory_unlock(12345);  -- разблокировка

-- Или транзакционная (авто-снимается при COMMIT/ROLLBACK):
SELECT pg_advisory_xact_lock(12345);
```

> Полезно для сериализации обработки saga по aggregate_id: только один
> воркер обрабатывает заказ 12345 одновременно.

### 9.7. JSONB для хранения saga-состояния и событий

```sql
-- Гибкое хранение payload
CREATE TABLE saga_instances (
    saga_id    UUID PRIMARY KEY,
    saga_type  VARCHAR(100),
    status     VARCHAR(20),
    payload    JSONB,            -- гибкая структура
    context    JSONB             -- промежуточные результаты шагов
);

-- Индекс по полям внутри JSONB
CREATE INDEX idx_saga_payload_order
    ON saga_instances ((payload->>'order_id'));

-- Запрос:
SELECT * FROM saga_instances WHERE payload->>'order_id' = '123';
```

---

## 10. Сравнение паттернов

| Критерий | 2PC | Saga (Choreography) | Saga (Orchestration) | Outbox | TCC | CQRS+ES |
|----------|-----|---------------------|----------------------|--------|-----|---------|
| **Гарантии** | Строгий ACID | Итоговая согласованность | Итоговая согласованность | Доставка событий | Строгий (бизнес-ACID) | Итоговая согласованность |
| **Блокировки** | Да (длительные) | Нет | Нет | Нет | Бизнес-резерв | Нет |
| **Производительность** | Низкая | Высокая | Высокая | Высокая | Средняя | Высокая |
| **Масштабируемость** | Плохая | Хорошая | Хорошая | Отличная | Средняя | Отличная |
| **Сложность** | Средняя | Средняя | Высокая | Низкая-Средняя | Высокая | Очень высокая |
| **Откат** | Автоматический | Компенсация | Компенсация | N/A (events) | Cancel | Компенсация / replay |
| **Изоляция** | Полная | Нет | Нет | Нет | Частичная | Нет |
| **SPOF** | Координатор | Нет | Оркестратор | Publisher | Координатор | Event store |
| **Подходит для** | Шардинг 1 БД | Простые потоки | Сложные потоки | Event-driven | Платежи/бронь | Аудит/сложный домен |
| **PostgreSQL-поддержка** | ✅ Встроенная | ✅ (таблицы) | ✅ (таблицы) | ✅ (FOR UPDATE SKIP LOCKED, CDC) | ✅ (таблицы) | ✅ (JSONB, event store) |

---

## 11. Рекомендуемая архитектура

### Для большинства микросервисных систем

```
                    ┌───────────────────────────────┐
                    │       API Gateway / Client     │
                    └───────────────┬───────────────┘
                                    │
                    ┌───────────────▼───────────────┐
                    │       Order Service            │
                    │  ┌─────────────────────────┐  │
                    │  │  PostgreSQL (Order DB)   │  │
                    │  │  • orders                │  │
                    │  │  • outbox                │  │
                    │  │  • saga_instances        │  │
                    │  └────────────┬────────────┘  │
                    │               │               │
                    │  ┌────────────▼────────────┐  │
                    │  │  Saga Orchestrator      │  │
                    │  └────────────┬────────────┘  │
                    └───────────────┼───────────────┘
                                    │
                         ┌──────────┼──────────┐
                         │          │          │
                    ┌────▼────┐ ┌───▼─────┐ ┌──▼──────┐
                    │ Payment │ │Inventory│ │Shipping │
                    │ Service │ │ Service │ │ Service │
                    │  + PG   │ │  + PG   │ │  + PG   │
                    │ +outbox │ │ +outbox │ │ +outbox │
                    └────┬────┘ └────┬────┘ └────┬────┘
                         │          │          │
                         └──────────┼──────────┘
                                    │
                    ┌───────────────▼───────────────┐
                    │      Kafka / RabbitMQ          │
                    │   (Event Bus / Message Broker) │
                    └───────────────────────────────┘
```

### Комбинированный подход

1. **Outbox Pattern** — для надёжной доставки событий (в каждом сервисе)
2. **Saga (Orchestration)** — для сложных многошаговых бизнес-процессов
3. **Идемпотентность** — на каждом потребителе (unique constraint + processed_events)
4. **CDC (Debezium)** — для чтения outbox с низкой задержкой (опционально, или polling)
5. **`FOR UPDATE SKIP LOCKED`** — для параллельной обработки outbox-воркерами
6. **Advisory Locks** — для сериализации операций по aggregate_id

### Когда что использовать

| Сценарий | Рекомендуемый паттерн |
|----------|----------------------|
| Простая операция из 2-3 шагов | Saga (Choreography) + Outbox |
| Сложный бизнес-процесс с условиями | Saga (Orchestration) + Outbox |
| Платежи, бронирования (строгие гарантии) | TCC |
| Event-driven архитектура, audit | CQRS + Event Sourcing + Outbox |
| Шардинг одной логической БД | 2PC (PREPARE TRANSACTION) |
| Аналитические запросы между БД | postgres_fdw (read-only) |
| Внутрисервисная асинхронность | LISTEN/NOTIFY |

---

## 12. Практический пример: e-commerce заказ

### Бизнес-сценарий

Пользователь оформляет заказ:
1. Создать заказ (Order Service)
2. Оплатить (Payment Service)
3. Зарезервировать товар (Inventory Service)
4. Назначить доставку (Shipping Service)

### Реализация: Saga + Outbox

#### Шаг 0: Инициализация saga (Order Service)

```sql
-- Order Service: одна транзакция
BEGIN;

-- 1. Создать заказ
INSERT INTO orders (id, customer_id, total, status, created_at)
VALUES ('ord-123', 'cust-456', 150.00, 'PENDING', NOW());

-- 2. Записать событие в outbox
INSERT INTO outbox (id, aggregate_id, aggregate_type, event_type, payload)
VALUES (
    gen_random_uuid(),
    'ord-123',
    'Order',
    'OrderCreated',
    jsonb_build_object(
        'orderId', 'ord-123',
        'customerId', 'cust-456',
        'total', 150.00,
        'items', jsonb_build_array(
            jsonb_build_object('sku', 'ITEM-1', 'qty', 2, 'price', 75.00)
        )
    )
);

-- 3. Создать saga
INSERT INTO saga_instances (saga_id, saga_type, current_step, status, payload)
VALUES (
    'saga-789',
    'order_creation',
    0,
    'RUNNING',
    jsonb_build_object('orderId', 'ord-123', 'total', 150.00)
);

COMMIT;
```

#### Шаг 1: Payment (Payment Service получает событие)

```sql
-- Payment Service: обработка OrderCreated
BEGIN;

-- Идемпотентность: проверить, не обработано ли уже
INSERT INTO processed_events (event_id, processed_at)
VALUES ('evt-001', NOW())
ON CONFLICT DO NOTHING
RETURNING event_id;
-- Если вернуло → обрабатываем; если нет → пропускаем

-- Обработка платежа
INSERT INTO payments (id, idempotency_key, order_id, amount, status)
VALUES ('pay-001', 'ord-123-pay', 'ord-123', 150.00, 'COMPLETED');

-- Событие в outbox
INSERT INTO outbox (id, aggregate_id, aggregate_type, event_type, payload)
VALUES (
    gen_random_uuid(), 'pay-001', 'Payment',
    'PaymentCompleted',
    jsonb_build_object('orderId', 'ord-123', 'paymentId', 'pay-001')
);

-- Обновить saga (через команду оркестратору, или событием)
INSERT INTO outbox (id, aggregate_id, aggregate_type, event_type, payload)
VALUES (
    gen_random_uuid(), 'saga-789', 'Saga',
    'SagaStepCompleted',
    jsonb_build_object('sagaId', 'saga-789', 'step', 1, 'result', 'SUCCESS')
);

COMMIT;
```

#### Шаг 2: Inventory (Inventory Service получает PaymentCompleted)

```sql
-- Inventory Service
BEGIN;

INSERT INTO processed_events (event_id, processed_at)
VALUES ('evt-002', NOW()) ON CONFLICT DO NOTHING RETURNING event_id;

-- Резервирование товара
UPDATE inventory
SET reserved_qty = reserved_qty + 2,
    available_qty = available_qty - 2
WHERE sku = 'ITEM-1' AND available_qty >= 2;

-- Проверка: если affected_rows = 0 → нехватка → запускаем компенсацию
-- (событие InventoryReservationFailed → оркестратор запускает RefundPayment)

-- Успех:
INSERT INTO outbox (id, aggregate_id, aggregate_type, event_type, payload)
VALUES (
    gen_random_uuid(), 'inv-001', 'Inventory',
    'InventoryReserved',
    jsonb_build_object('orderId', 'ord-123', 'items', ...)
);

COMMIT;
```

#### Шаг 3: Shipping (Shipping Service получает InventoryReserved)

```sql
-- Shipping Service
BEGIN;

INSERT INTO processed_events (event_id, processed_at)
VALUES ('evt-003', NOW()) ON CONFLICT DO NOTHING RETURNING event_id;

INSERT INTO shipments (id, order_id, status, tracking_id, created_at)
VALUES ('ship-001', 'ord-123', 'SCHEDULED', 'TRK-999', NOW());

INSERT INTO outbox (id, aggregate_id, aggregate_type, event_type, payload)
VALUES (
    gen_random_uuid(), 'ship-001', 'Shipping',
    'OrderShipped',
    jsonb_build_object('orderId', 'ord-123', 'trackingId', 'TRK-999')
);

COMMIT;
```

#### Оркестратор обновляет saga

```sql
-- При получении каждого SagaStepCompleted:
UPDATE saga_instances
SET current_step = current_step + 1,
    updated_at = NOW(),
    status = CASE
        WHEN current_step + 1 >= 4 THEN 'COMPLETED'
        ELSE 'RUNNING'
    END,
    context = jsonb_set(context, $$steps.$$ || (current_step + 1)::text,
        jsonb_build_object('status', 'COMPLETED'))
WHERE saga_id = 'saga-789';
```

#### Компенсация: сбой на Shipping

Если Shipping Service не может назначить доставку:

```sql
-- Shipping Service: событие об ошибке
INSERT INTO outbox (id, aggregate_type, event_type, payload)
VALUES (
    gen_random_uuid(), 'Shipping',
    'ShippingFailed',
    jsonb_build_object('orderId', 'ord-123', 'reason', 'NO_CARRIER_AVAILABLE')
);

-- Оркестратор при получении ShippingFailed запускает компенсации:

-- Компенсация 1: Release Inventory
-- → Inventory Service: UPDATE inventory SET reserved_qty -= 2, available_qty += 2

-- Компенсация 2: Refund Payment
-- → Payment Service:
INSERT INTO payments (id, idempotency_key, order_id, amount, type, status)
VALUES ('pay-002', 'ord-123-refund', 'ord-123', 150.00, 'REFUND', 'COMPLETED');

-- Компенсация 3: Reject Order
-- → Order Service:
UPDATE orders SET status = 'CANCELLED' WHERE id = 'ord-123';

-- Saga помечается как FAILED (с компенсациями)
UPDATE saga_instances
SET status = 'COMPENSATED', updated_at = NOW()
WHERE saga_id = 'saga-789';
```

### Outbox Publisher (фоновый процесс в каждом сервисе)

```sql
-- Воркер читает outbox и публикует в Kafka
BEGIN;

SELECT id, aggregate_id, event_type, payload
FROM outbox
WHERE processed_at IS NULL
ORDER BY created_at
LIMIT 100
FOR UPDATE SKIP LOCKED;

-- Для каждой записи: publish to Kafka topic
-- (например: topic = aggregate_type, key = aggregate_id)

UPDATE outbox
SET processed_at = NOW()
WHERE id IN (...);

COMMIT;
```

---

## 13. Антипаттерны

### ❌ 1. Распределённая транзакция через HTTP-вызовы

```
Order Service → HTTP POST → Payment Service → HTTP POST → Inventory Service
```

Проблемы: нет атомарности, сбой сети посередине = несогласованное состояние,
невозможно откатить уже выполненные шаги.

### ❌ 2. Общая база данных для нескольких сервисов

```
Order Service ──┐
Payment Service ─┼──► одна PostgreSQL
Inventory Service┘
```

Проблемы: нарушает принцип Database-per-service, создаёт coupling,
невозможно независимо масштабировать, schema-конфликты.

> Допустимо только на этапе перехода от монолита (strangler fig pattern).

### ❌ 3. Длительные распределённые блокировки

Использование `SELECT ... FOR UPDATE` на одной БД для координации с другой —
приводит к deadlocks и зависаниям.

### ❌ 4. Синхронные цепочки вызовов без таймаутов

```
A → B → C → D → E (все синхронно, без таймаутов)
```

Сбой на E блокирует все вышележащие сервисы (cascade failure).

### ❌ 5. Игнорирование идемпотентности

Если потребитель не проверяет `idempotency_key` — дубликаты сообщений
приводят к двойным платежам, двойным заказам и т.д.

### ❌ 6. Использование 2PC для микросервисов

2PC требует общей транзакционной координации — противоречит автономности
микросервисов. Блокировки «замораживают» ресурсы на время всей транзакции.

### ❌ 7. События без версионирования

События без `version` / `schema_version` — при изменении формата события
старые потребители ломаются. Всегда включайте версию в событие.

---

## 14. Глоссарий

| Термин | Определение |
|--------|-------------|
| **ACID** | Atomicity, Consistency, Isolation, Durability — свойства транзакций |
| **BASE** | Basically Available, Soft state, Eventual consistency |
| **2PC** | Two-Phase Commit — протокол распределённого коммита с координатором |
| **Saga** | Последовательность локальных транзакций с компенсациями |
| **Outbox** | Паттерн атомарной записи бизнес-данных и события в одну БД |
| **CDC** | Change Data Capture — чтение изменений из WAL (logical decoding) |
| **CQRS** | Command Query Responsibility Segregation — разделение записи и чтения |
| **Event Sourcing** | Хранение истории событий вместо текущего состояния |
| **TCC** | Try-Confirm-Cancel — двухфазный протокол на бизнес-уровне |
| **Идемпотентность** | Свойство операции: повторное выполнение даёт тот же результат |
| **Компенсация** | Бизнес-операция, логически отменяющая предыдущую |
| **Итоговая согласованность** | Согласованность достигается со временем, не мгновенно |
| **PREPARE TRANSACTION** | PostgreSQL-команда для 2PC (подготовленная транзакция) |
| **postgres_fdw** | Foreign Data Wrapper для доступа к удалённым PostgreSQL |
| **LISTEN/NOTIFY** | Встроенный pub/sub в PostgreSQL |
| **FOR UPDATE SKIP LOCKED** | PostgreSQL: блокировка строк с пропуском уже заблокированных |
| **Advisory Lock** | Блокировка на уровне приложения (по ключу, не по строке) |
| **Logical Decoding** | Чтение потока изменений из WAL PostgreSQL |
| **WAL** | Write-Ahead Log — журнал предзаписи PostgreSQL |
| **Debezium** | Инструмент CDC для PostgreSQL → Kafka |
| **At-least-once** | Гарантия доставки: сообщение доставляется ≥1 раз (возможны дубли) |
| **Exactly-once** | Гарантия доставки: сообщение доставляется ровно 1 раз (очень сложно) |
| **SPOF** | Single Point of Failure — единая точка отказа |

---

*Исследование подготовлено на основе анализа паттернов распределённых транзакций,
PostgreSQL-документации и лучших практик микросервисной архитектуры.*