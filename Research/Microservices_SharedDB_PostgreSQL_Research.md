# Исследование: Транзакции между микросервисами с единой базой данных PostgreSQL (Shared Database)

## Оглавление
1. [Введение: Shared Database Pattern](#1-введение-shared-database-pattern)
2. [Когда применять единую БД](#2-когда-применять-единую-бд)
3. [Архитектура: микросервисы над одной PostgreSQL](#3-архитектура-микросервисы-над-одной-postgresql)
4. [Разделение по схемам (Schema-per-service)](#4-разделение-по-схемам-schema-per-service)
5. [Транзакции в единой БД: возврат к ACID](#5-транзакции-в-единой-бд-возврат-к-acid)
6. [Кросс-сервисные транзакции: паттерны](#6-кросс-сервисные-транзакции-паттерны)
7. [Outbox Pattern в единой БД](#7-outbox-pattern-в-единой-бд)
8. [Идемпотентность и конкурентность](#8-идемпотентность-и-конкурентность)
9. [PostgreSQL-механизмы для координации](#9-postgresql-механизмы-для-координации)
10. [Изоляция сервисов: ограничения и политики](#10-изоляция-сервисов-ограничения-и-политики)
11. [Миграция от монолита к микросервисам](#11-миграция-от-монолита-к-микросервисам)
12. [Сравнение: Shared DB vs Database-per-service](#12-сравнение-shared-db-vs-database-per-service)
13. [Антипаттерны](#13-антипаттерны)
14. [Рекомендуемая архитектура](#14-рекомендуемая-архитектура)
15. [Глоссарий](#15-глоссарий)

---

## 1. Введение: Shared Database Pattern

В классической микросервисной архитектуре применяется принцип **Database-per-service**:
каждый сервис имеет собственную БД. Однако существует альтернативный паттерн —
**Shared Database (интегрированная БД)**, при котором несколько микросервисов работают
с **одной и той же** PostgreSQL.

```
                    ┌────────────────────────────────────────┐
                    │          API Gateway / Client           │
                    └───────────────────┬────────────────────┘
                                        │
           ┌────────────────────────────┼────────────────────────────┐
           │                            │                            │
    ┌──────▼──────┐             ┌───────▼───────┐            ┌───────▼───────┐
    │    Order     │             │    Payment     │            │  Inventory    │
    │   Service    │             │    Service     │            │   Service     │
    └──────┬───────┘             └───────┬────────┘            └───────┬────────┘
           │                            │                            │
           │         ┌──────────────────▼──────────────────┐          │
           └────────►│         Единая PostgreSQL            │◄─────────┘
                     │  ┌─────────┐ ┌─────────┐ ┌────────┐ │
                     │  │orders   │ │payments │ │inventor│ │
                     │  │(schema) │ │(schema) │ │(schema)│ │
                     │  └─────────┘ └─────────┘ └────────┘ │
                     │            outbox (schema)          │
                     └─────────────────────────────────────┘
```

### Почему этот паттерн важен

Единая БД возвращает разработчику главное преимущество монолита — **ACID-транзакции**.
Операция, затрагивающая несколько сервисов, может выполняться в **одной транзакции**,
что кардинально упрощает обеспечение целостности данных.

> Ключевой тезис: Shared Database — это **компромисс** между простотой монолита
> и гибкостью микросервисов. Он **не является каноническим** микросервисным паттерном,
> но широко применяется на практике, особенно на этапе эволюции от монолита.

---

## 2. Когда применять единую БД

### ✅ Подходящие сценарии

| Сценарий | Обоснование |
|----------|-------------|
| **Эволюция монолита → микросервисы** | Strangler Fig: извлекаем сервисы, но БД пока общая |
| **Стартап / MVP** | Быстрый старт, низкая операционная сложность |
| **Малые и средние команды** | Нет ресурсов на support нескольких БД |
| **Сильная связанность данных** | Сервисы часто обращаются к общим данным |
| **Требования строгой согласованности** | Платежи, учёт, склад — где eventual consistency неприемлема |
| **Отчётность и аналитика** | JOIN-ы между доменами без ETL |
| **Команда не готова к распределённым транзакциям** | Saga/Outbox/CDC слишком сложны на текущем этапе |

### ❌ Когда НЕ применять

| Сценарий | Обоснование |
|----------|-------------|
| **Высокая нагрузка / большой масштаб** | Единая БД — узкое место (CPU, I/O, блокировки) |
| **Команды независимы** | Нужна schema-автономия, разные циклы релизов БД |
| **Разные требования к БД** | Один сервис — OLTP, другой — OLAP |
| **Жёсткая изоляция по безопасности** | Один tenant не должен видеть данные другого |
| **Полимерные данные (poliglot persistence)** | Нужны Redis, MongoDB, Elasticsearch и т.д. |
| **Микросервисы как «цель», не как «этап»** | Зрелая микросервисная архитектура требует независимости |

### Зрелость подхода

```
Монолит (1 БД, 1 приложение)
    │
    ▼
Shared Database (1 БД, N сервисов)  ← мы здесь
    │
    ▼
Schema-per-service (1 БД, N схем, N сервисов)
    │
    ▼
Database-per-service (N БД, N сервисов)  ← канонические микросервисы
```

---

## 3. Архитектура: микросервисы над одной PostgreSQL

### Уровни разделения

При единой БД разделение может происходить на разных уровнях:

| Уровень | Разделение | Изоляция | Сложность |
|---------|-----------|----------|-----------|
| **Таблицы** | Каждый сервис работает со своими таблицами в одной схеме | Низкая | Низкая |
| **Схемы (Schema-per-service)** | Каждый сервис имеет собственную схему (namespace) | Средняя | Средняя |
| **Roles + Grants** | Каждый сервис подключается под своей ролью с ограниченными правами | Средняя-Высокая | Средняя |
| **Все вместе** | Schema + Role + Grant + Row-Level Security | Высокая | Высокая |

### Рекомендуемый подход: Schema-per-service + Role-per-service

```
PostgreSQL
├── Schema: orders_service
│   ├── Role: orders_app (доступ только к orders_service)
│   ├── Tables: orders, order_items, order_status_history
│   └── ...
├── Schema: payments_service
│   ├── Role: payments_app
│   ├── Tables: payments, refunds, payment_methods
│   └── ...
├── Schema: inventory_service
│   ├── Role: inventory_app
│   ├── Tables: products, stock_items, reservations
│   └── ...
└── Schema: outbox
    ├── Role: outbox_publisher (чтение), сервисы (запись)
    └── Tables: outbox_events
```

---

## 4. Разделение по схемам (Schema-per-service)

### Создание изолированных схем и ролей

```sql
-- ── Роли для каждого сервиса ──
CREATE ROLE orders_app LOGIN PASSWORD '...';
CREATE ROLE payments_app LOGIN PASSWORD '...';
CREATE ROLE inventory_app LOGIN PASSWORD '...';
CREATE ROLE outbox_publisher LOGIN PASSWORD '...';

-- ── Схема Order Service ──
CREATE SCHEMA orders_service AUTHORIZATION orders_app;

CREATE TABLE orders_service.orders (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    customer_id UUID NOT NULL,
    total       DECIMAL(10,2) NOT NULL,
    status      VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    created_at  TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE orders_service.order_items (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id    UUID NOT NULL REFERENCES orders_service.orders(id),
    sku         VARCHAR(50) NOT NULL,
    quantity    INT NOT NULL,
    unit_price  DECIMAL(10,2) NOT NULL
);

-- Права: только orders_app работает с этой схемой
GRANT USAGE ON SCHEMA orders_service TO orders_app;
GRANT SELECT, INSERT, UPDATE, DELETE
    ON ALL TABLES IN SCHEMA orders_service TO orders_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA orders_service
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO orders_app;

-- ── Схема Payment Service ──
CREATE SCHEMA payments_service AUTHORIZATION payments_app;

CREATE TABLE payments_service.payments (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    order_id        UUID NOT NULL,
    amount          DECIMAL(10,2) NOT NULL,
    status          VARCHAR(20) NOT NULL DEFAULT 'PENDING',
    idempotency_key VARCHAR(100) UNIQUE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

GRANT USAGE ON SCHEMA payments_service TO payments_app;
GRANT SELECT, INSERT, UPDATE, DELETE
    ON ALL TABLES IN SCHEMA payments_service TO payments_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA payments_service
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO payments_app;

-- ── Схема Inventory Service ──
CREATE SCHEMA inventory_service AUTHORIZATION inventory_app;

CREATE TABLE inventory_service.products (
    sku            VARCHAR(50) PRIMARY KEY,
    name           VARCHAR(200) NOT NULL,
    available_qty  INT NOT NULL DEFAULT 0,
    reserved_qty   INT NOT NULL DEFAULT 0
);

GRANT USAGE ON SCHEMA inventory_service TO inventory_app;
GRANT SELECT, INSERT, UPDATE, DELETE
    ON ALL TABLES IN SCHEMA inventory_service TO inventory_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA inventory_service
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO inventory_app;
```

### Cross-schema ссылки: можно, но осторожно

PostgreSQL позволяет FOREIGN KEY между схемами:

```sql
-- Payment ссылается на Order (между схемами)
ALTER TABLE payments_service.payments
ADD CONSTRAINT fk_payment_order
FOREIGN KEY (order_id) REFERENCES orders_service.orders(id);
```

> ⚠️ Это создаёт **coupling** между сервисами на уровне БД. Решение:
> - Разрешить cross-schema FK для **только reference-данных** (ID, коды)
> - Не разрешать FK на «рабочие» таблицы с частыми изменениями
> - Или использовать «мягкие» ссылки (без FK, проверка в приложении)

### Чтение чужих данных: READ-ONLY grant

Часто сервису нужно **прочитать** данные другого сервиса (например, Order Service
читает статус платежа). Вместо API-вызова — прямой read-only доступ:

```sql
-- Order Service может читать payments (но не писать)
GRANT USAGE ON SCHEMA payments_service TO orders_app;
GRANT SELECT ON payments_service.payments TO orders_app;

-- Запрос внутри Order Service:
SELECT status FROM payments_service.payments WHERE order_id = ?;
```

> Альтернатива: каждый сервис предоставляет API для чтения. Прямой READ-доступ —
> компромисс ради производительности (нет сетевого вызова, нет сериализации).

---

## 5. Транзакции в единой БД: возврат к ACID

### Главное преимущество

В единой PostgreSQL **кросс-сервисная операция — это обычная транзакция**:

```sql
-- Order Service: оформить заказ + оплату + резерв товара
-- ВСЁ В ОДНОЙ ТРАНЗАКЦИИ
BEGIN;

-- 1. Создать заказ (схема orders_service)
INSERT INTO orders_service.orders (id, customer_id, total, status)
VALUES ('ord-123', 'cust-456', 150.00, 'PENDING');

INSERT INTO orders_service.order_items (order_id, sku, quantity, unit_price)
VALUES ('ord-123', 'ITEM-1', 2, 75.00);

-- 2. Создать платёж (схема payments_service)
INSERT INTO payments_service.payments (id, order_id, amount, status)
VALUES ('pay-001', 'ord-123', 150.00, 'COMPLETED');

-- 3. Зарезервировать товар (схема inventory_service)
UPDATE inventory_service.products
SET reserved_qty = reserved_qty + 2,
    available_qty = available_qty - 2
WHERE sku = 'ITEM-1' AND available_qty >= 2;

-- Проверяем, что резерв удался
-- (если affected_rows = 0 → ROLLBACK)

-- 4. Подтвердить заказ
UPDATE orders_service.orders SET status = 'CONFIRMED' WHERE id = 'ord-123';

COMMIT;  -- Всё или ничего — настоящий ACID
```

### Сравнение с Database-per-service

| Аспект | Database-per-service | Shared Database (1 PostgreSQL) |
|--------|----------------------|--------------------------------|
| Атомарность | Saga / 2PC (сложно) | **Одна транзакция (просто)** |
| Изоляция | Н/Д (разные БД) | Уровни изоляции PostgreSQL |
| Согласованность | Итоговая (eventual) | **Мгновенная (strong)** |
| Откат | Компенсирующие операции | **ROLLBACK** |
| Сложность | Высокая | **Низкая** |
| Производительность | Высокая (нет распределённых блокировок) | Зависит от contention |
| Масштабируемость | Отличная | Ограничена одной БД |

### Уровни изоляции

Единая БД позволяет использовать стандартные уровни изоляции PostgreSQL:

| Уровень | Аномалии | Рекомендация |
|---------|----------|--------------|
| `READ COMMITTED` (по умолчанию) | Non-repeatable read, phantom | Подходит для большинства |
| `REPEATABLE READ` | Защита от non-repeatable read, phantom | Для отчётов, длительных чтений |
| `SERIALIZABLE` | Полная изоляция | Для критичных финансовых операций |

```sql
-- Установить уровень изоляции для транзакции
BEGIN ISOLATION LEVEL SERIALIZABLE;
-- ... операции ...
COMMIT;
```

> `SERIALIZABLE` в PostgreSQL использует SSI (Serializable Snapshot Isolation) —
> не использует блокировки, а откатывает транзакции при конфликте. Нужно
> обрабатывать `serialization_failure` (SQLSTATE 40001) с retry.

---

## 6. Кросс-сервисные транзакции: паттерны

### Паттерн 1: Прямая транзакция (Inline Transaction)

Сервис-инициатор выполняет всю работу в одной транзакции, напрямую обращаясь
к схемам других сервисов.

```
Order Service (транзакция)
├── INSERT в orders_service
├── INSERT в payments_service   ← прямой доступ
└── UPDATE в inventory_service  ← прямой доступ
```

```sql
-- Order Service подключается под ролью с расширенными правами
-- или использует SECURITY DEFINER функции (см. ниже)
BEGIN;
INSERT INTO orders_service.orders ...;
INSERT INTO payments_service.payments ...;
UPDATE inventory_service.products ...;
COMMIT;
```

**Плюсы:** максимальная простота, ACID
**Минусы:** сервис знает о схемах других сервисов → coupling; нужна роль
с правами на несколько схем

### Паттерн 2: Stored Procedure / SECURITY DEFINER

Инкапсуляция кросс-сервисной логики в **хранимой процедуре** с правами владельца
(SECURITY DEFINER):

```sql
-- Процедура в отдельной схеме, принадлежит суперпользователю БД
CREATE SCHEMA shared_procedures AUTHORIZATION postgres;

CREATE OR REPLACE FUNCTION shared_procedures.create_order_with_payment(
    p_customer_id UUID,
    p_items JSONB,          -- [{sku, qty, price}, ...]
    p_payment_method VARCHAR(50)
) RETURNS UUID
LANGUAGE plpgsql
SECURITY DEFINER           -- выполняется с правами создателя
SET search_path = orders_service, payments_service, inventory_service
AS $$
DECLARE
    v_order_id UUID := gen_random_uuid();
    v_payment_id UUID := gen_random_uuid();
    v_total DECIMAL(10,2) := 0;
    item JSONB;
BEGIN
    -- Вычисляем итог
    SELECT COALESCE(SUM((item->>'price')::DECIMAL * (item->>'qty')::INT), 0)
    INTO v_total
    FROM jsonb_array_elements(p_items) AS item;

    -- 1. Создать заказ
    INSERT INTO orders_service.orders (id, customer_id, total, status)
    VALUES (v_order_id, p_customer_id, v_total, 'PENDING');

    -- 2. Создать позиции заказа
    INSERT INTO orders_service.order_items (order_id, sku, quantity, unit_price)
    SELECT v_order_id,
           item->>'sku',
           (item->>'qty')::INT,
           (item->>'price')::DECIMAL
    FROM jsonb_array_elements(p_items) AS item;

    -- 3. Резерв товара
    FOR item IN SELECT jsonb_array_elements(p_items) LOOP
        UPDATE inventory_service.products
        SET reserved_qty = reserved_qty + (item->>'qty')::INT,
            available_qty = available_qty - (item->>'qty')::INT
        WHERE sku = item->>'sku'
          AND available_qty >= (item->>'qty')::INT;

        IF NOT FOUND THEN
            RAISE EXCEPTION 'Insufficient stock for SKU: %', item->>'sku'
            USING ERRCODE = 'P0001';
        END IF;
    END LOOP;

    -- 4. Платёж
    INSERT INTO payments_service.payments (id, order_id, amount, status)
    VALUES (v_payment_id, v_order_id, v_total, 'COMPLETED');

    -- 5. Подтвердить заказ
    UPDATE orders_service.orders SET status = 'CONFIRMED'
    WHERE id = v_order_id;

    RETURN v_order_id;
END;
$$;

-- Дать право вызова сервисам
GRANT EXECUTE ON FUNCTION shared_procedures.create_order_with_payment(UUID, JSONB, VARCHAR)
    TO orders_app;
```

```sql
-- Вызов из Order Service (простой):
SELECT shared_procedures.create_order_with_payment(
    'cust-456',
    '[{"sku":"ITEM-1","qty":2,"price":75.00}]'::jsonb,
    'CARD'
);
-- Вся операция — атомарная транзакция
```

**Плюсы:** инкапсуляция логики, сервисы не знают детали схем, ACID-гарантии
**Минусы:** бизнес-логика в БД (трудно тестировать, версионировать); plpgsql — не «модный» стек

### Паттерн 3: API-вызовы + одна БД (гибридный)

Сервисы общаются через HTTP/gRPC, но поскольку БД общая — сервис-получатель
выполняет свою часть в своей транзакции. Целостность достигается через
**сагу + outbox** (как при database-per-service), но без распределённых проблем,
т.к. все данные в одной БД.

```
Order Service                Payment Service            Inventory Service
     │                            │                           │
     │── POST /payments ────────►│                           │
     │                            │ INSERT в payments_service │
     │                            │ (своя транзакция)          │
     │◄─── 200 OK ───────────────│                           │
     │                                                        │
     │── POST /inventory/reserve ──────────────────────────►│
     │                            UPDATE в inventory_service │
     │◄─── 200 OK ─────────────────────────────────────────│
     │                                                        │
     │  UPDATE orders SET status = 'CONFIRMED'               │
```

> Здесь **нет единой транзакции** — каждый сервис коммитит отдельно.
> Если inventory падает после payments — нужен откат платежа (компенсация).
> Но поскольку БД одна — компенсация проще и можно использовать
> **advisory locks** и **read-after-write** для согласованности.

### Сводная таблица паттернов

| Паттерн | ACID | Coupling | Сложность | Производительность |
|---------|------|----------|-----------|-------------------|
| Прямая транзакция | ✅ | Высокая | Низкая | Высокая |
| Stored Procedure | ✅ | Средняя | Средняя | Высокая |
| API + Outbox/Saga | Итоговая | Низкая | Высокая | Средняя |

---

## 7. Outbox Pattern в единой БД

### Зачем Outbox при единой БД?

Даже при единой БД нужна **надёжная доставка событий** во внешние системы
(Kafka, RabbitMQ, другие сервисы через брокер). Outbox решает ту же проблему:
атомарно записать данные + событие.

### Схема outbox (общая для всех сервисов)

```sql
CREATE SCHEMA outbox AUTHORIZATION postgres;

CREATE TABLE outbox.outbox_events (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_service  VARCHAR(50) NOT NULL,       -- 'order', 'payment', 'inventory'
    aggregate_id    UUID NOT NULL,
    aggregate_type  VARCHAR(50) NOT NULL,
    event_type      VARCHAR(100) NOT NULL,
    payload         JSONB NOT NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    processed_at    TIMESTAMPTZ,
    retry_count     INT NOT NULL DEFAULT 0
);

CREATE INDEX idx_outbox_unprocessed
    ON outbox.outbox_events (created_at)
    WHERE processed_at IS NULL;

-- Каждый сервис может писать в outbox
GRANT INSERT ON outbox.outbox_events TO orders_app, payments_app, inventory_app;
GRANT USAGE ON SCHEMA outbox TO orders_app, payments_app, inventory_app;

-- Publisher читает и обновляет
GRANT SELECT, UPDATE ON outbox.outbox_events TO outbox_publisher;
```

### Использование в транзакции

```sql
-- Order Service: атомарно создать заказ и событие
BEGIN;

INSERT INTO orders_service.orders (id, customer_id, total, status)
VALUES ('ord-123', 'cust-456', 150.00, 'CONFIRMED');

INSERT INTO outbox.outbox_events (
    source_service, aggregate_id, aggregate_type, event_type, payload
) VALUES (
    'order', 'ord-123', 'Order', 'OrderConfirmed',
    jsonb_build_object('orderId', 'ord-123', 'total', 150.00)
);

COMMIT;
-- Событие гарантированно записано вместе с заказом
```

### Publisher: FOR UPDATE SKIP LOCKED

```sql
-- Outbox Publisher (отдельный процесс):
BEGIN;

SELECT id, source_service, event_type, payload
FROM outbox.outbox_events
WHERE processed_at IS NULL
ORDER BY created_at
LIMIT 100
FOR UPDATE SKIP LOCKED;

-- Для каждого: отправить в Kafka/RabbitMQ
-- Затем:
UPDATE outbox.outbox_events
SET processed_at = NOW()
WHERE id = ANY(?);

COMMIT;
```

### LISTEN/NOTIFY как триггер

В единой БД можно использовать `LISTEN/NOTIFY` для **мгновенного** пробуждения
publisher без polling:

```sql
-- Триггер на outbox (или в приложении после INSERT):
CREATE OR REPLACE FUNCTION outbox.notify_new_event()
RETURNS TRIGGER LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_notify('outbox_events', NEW.id::text);
    RETURN NEW;
END;
$$;

CREATE TRIGGER trg_outbox_notify
    AFTER INSERT ON outbox.outbox_events
    FOR EACH ROW EXECUTE FUNCTION outbox.notify_new_event();

-- Publisher:
LISTEN outbox_events;
-- При получении notification → SELECT из outbox WHERE processed_at IS NULL
```

> `LISTEN/NOTIFY` доставляется только при COMMIT, что согласуется с транзакционной
> семантикой. Однако уведомления **не сохраняются** — если publisher был офлайн,
> он пропустит событие. Поэтому LISTEN/NOTIFY = «быстрый будильник»,
> а polling/CDC = «гарантия доставки».

---

## 8. Идемпотентность и конкурентность

### Идемпотентность при единой БД

Даже в единой БД сервисы могут получать **дублирующие** запросы (retry от
HTTP-клиента, брокер at-least-once). Идемпотентность обязательна.

```sql
-- Уникальный ключ идемпотентности
ALTER TABLE payments_service.payments
ADD COLUMN idempotency_key VARCHAR(100) UNIQUE;

-- Upsert: безопасный повтор
INSERT INTO payments_service.payments
    (id, order_id, amount, status, idempotency_key)
VALUES ('pay-001', 'ord-123', 150.00, 'COMPLETED', 'req-abc-123')
ON CONFLICT (idempotency_key) DO NOTHING
RETURNING id;
-- Если вернул строку → новая операция
-- Если ничего → уже обработана → вернуть кэшированный результат
```

### Таблица обработанных запросов

```sql
CREATE TABLE orders_service.processed_requests (
    idempotency_key  VARCHAR(100) PRIMARY KEY,
    response_data    JSONB,
    processed_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Перед выполнением:
BEGIN;
INSERT INTO orders_service.processed_requests (idempotency_key)
VALUES ('req-abc-123')
ON CONFLICT DO NOTHING
RETURNING idempotency_key;
-- Если вернуло ключ → выполняем операцию
-- Если NULL → уже было → возвращаем response_data
COMMIT;
```

### Конкурентность: оптимистичная блокировка

```sql
-- Версионирование сущности
ALTER TABLE orders_service.orders ADD COLUMN version INT NOT NULL DEFAULT 0;

-- Обновление с проверкой версии
UPDATE orders_service.orders
SET status = 'SHIPPED', version = version + 1
WHERE id = 'ord-123' AND version = 5;
-- affected_rows = 0 → кто-то изменил ранее → конфликт → retry
```

### Конкурентность: advisory locks для сериализации

```sql
-- Сериализация операций по order_id (только один процесс за раз)
BEGIN;
SELECT pg_advisory_xact_lock(
    hashtext('ord-123')  -- числовой ключ из строки
);
-- ... работа с заказом ...
COMMIT;  -- блокировка снимается автоматически
```

> Advisory locks удобны для «критических секций» — например, чтобы два
> запроса не резервировали один и тот же товар одновременно.

---

## 9. PostgreSQL-механизмы для координации

### 9.1. Advisory Locks

```sql
-- Транзакционная (снимается при COMMIT/ROLLBACK)
SELECT pg_advisory_xact_lock(12345);

-- Сессионная (снимается явно или при разрыве соединения)
SELECT pg_advisory_lock(12345);
SELECT pg_advisory_unlock(12345);

-- По строковому ключу (через хэш)
SELECT pg_advisory_xact_lock(hashtext('order:ord-123'));
```

Применение: сериализация обработки одного aggregate, защита от двойного выполнения,
распределённые «мьютексы».

### 9.2. SELECT FOR UPDATE (пессимистичная блокировка)

```sql
-- Блокировать строку на чтение-запись до конца транзакции
BEGIN;
SELECT * FROM orders_service.orders
WHERE id = 'ord-123'
FOR UPDATE;       -- никто другой не может UPDATE/DELETE
-- ... изменения ...
COMMIT;
```

Варианты:
- `FOR UPDATE NOWAIT` — сразу ошибка, если заблокировано
- `FOR UPDATE SKIP LOCKED` — пропустить заблокированные строки
- `FOR NO KEY UPDATE` — мягче, разрешает SELECT FOR SHARE

### 9.3. SAVEPOINT (частичный откат)

```sql
BEGIN;
INSERT INTO orders_service.orders ...;

SAVEPOINT sp_payment;
BEGIN;
    -- Попытка платежа
    INSERT INTO payments_service.payments ...;
EXCEPTION WHEN OTHERS THEN
    ROLLBACK TO sp_payment;
    -- Платёж не прошёл, но заказ остаётся → можно записать статус 'PAYMENT_FAILED'
END;

-- Заказ создан, платёж провален — транзакция продолжается
UPDATE orders_service.orders SET status = 'PAYMENT_FAILED' WHERE id = 'ord-123';
COMMIT;
```

> SAVEPOINT позволяет «провалить» часть транзакции, не откатывая всё. Полезно
> для try-confirm логики внутри одной БД.

### 9.4. Row-Level Security (RLS)

Дополнительная изоляция: каждый сервис видит **только свои строки**:

```sql
-- Включить RLS
ALTER TABLE orders_service.orders ENABLE ROW LEVEL SECURITY;

-- Политика: orders_app видит все строки (свой сервис)
CREATE POLICY orders_all ON orders_service.orders
    FOR ALL TO orders_app
    USING (true);

-- Другие сервисы (например, payments_app) видят только оплаченные заказы
CREATE POLICY payments_read_confirmed ON orders_service.orders
    FOR SELECT TO payments_app
    USING (status IN ('CONFIRMED', 'SHIPPED', 'DELIVERED'));
```

### 9.5. LISTEN/NOTIFY (асинхронные уведомления)

```sql
-- Сервис A: в транзакции
BEGIN;
INSERT INTO inventory_service.products (sku, name, available_qty)
VALUES ('ITEM-2', 'Gadget', 100);
NOTIFY inventory_events, '{"event":"ProductAdded","sku":"ITEM-2"}';
COMMIT;
-- Уведомление отправляется при COMMIT

-- Сервис B: слушает
LISTEN inventory_events;
-- Приложение получает уведомление → может обновить кэш, запустить процесс
```

### 9.6. Materialized Views (для read-моделей)

```sql
-- Объединённое представление для Order Service (read-optimized)
CREATE MATERIALIZED VIEW orders_service.order_summary AS
SELECT
    o.id AS order_id,
    o.customer_id,
    o.total,
    o.status AS order_status,
    p.status AS payment_status,
    p.id AS payment_id
FROM orders_service.orders o
LEFT JOIN payments_service.payments p ON p.order_id = o.id
WITH DATA;

-- Индекс
CREATE UNIQUE INDEX idx_order_summary_id
    ON orders_service.order_summary (order_id);

-- Обновление (по расписанию или триггером)
REFRESH MATERIALIZED VIEW CONCURRENTLY orders_service.order_summary;
```

> `CONCURRENTLY` — не блокирует чтение во время обновления.

---

## 10. Изоляция сервисов: ограничения и политики

### Проблема: «все могут всё»

Главная слабость единой БД — **соблазн** обратиться к чужим таблицам напрямую,
минуя API. Это разрушает инкапсуляцию сервисов.

### Уровни защиты

| Средство | Что защищает | Сложность |
|----------|-------------|-----------|
| **Schema + Role + GRANT** | Сервис не может писать в чужую схему | Базовая |
| **READ-only GRANT** | Сервис может читать, но не писать чужие данные | Средняя |
| **Row-Level Security** | Сервис видит только разрешённые строки | Высокая |
| **SECURITY DEFINER функции** | Логика инкапсулирована, доступ только через API | Высокая |
| **View-слой** | Сервисы обращаются к view, не к таблицам | Средняя |
| **Application-level договорённости** | Команда约定: «не лезть в чужие схемы» | Низкая (ненадёжная) |

### Политика доступа (рекомендация)

```
┌───────────────────────────────────────────────────────────┐
│  Правило: сервис пишет ТОЛЬКО в свою схему                │
│           сервис читает чужие схемы только через:         │
│             а) READ-only GRANT на view                    │
│             б) SECURITY DEFINER функции                   │
│             в) API-вызов (для сложной логики)             │
├───────────────────────────────────────────────────────────┤
│  Cross-schema транзакции: только через                    │
│  SECURITY DEFINER функции в shared_procedures             │
└───────────────────────────────────────────────────────────┘
```

```sql
-- Вместо прямого доступа к таблице — view
CREATE VIEW payments_service.payment_status_v AS
SELECT order_id, status, amount, created_at
FROM payments_service.payments;

GRANT SELECT ON payments_service.payment_status_v TO orders_app;
-- orders_app видит только view, не таблицу
REVOKE ALL ON payments_service.payments FROM orders_app;
```

---

## 11. Миграция от монолита к микросервисам

### Strangler Fig Pattern с единой БД

```
Этап 0: Монолит
┌──────────────────────────────┐
│       Монолит (1 app)         │
│       1 PostgreSQL            │
│       1 схема (public)        │
└──────────────────────────────┘

Этап 1: Выделение первого сервиса (Shared DB)
┌──────────────┐  ┌──────────────────────┐
│  Order        │  │  Монолит (остальное)  │
│  Service      │  │                      │
│  schema:      │  │  schema: public      │
│  orders_svc   │  │                      │
└──────┬────────┘  └──────────┬───────────┘
       │       1 PostgreSQL    │
       └──────────┬────────────┘
                  ▼
           Единая PostgreSQL

Этап 2: Выделение ещё сервисов
┌──────────┐ ┌──────────┐ ┌──────────┐
│  Order   │ │ Payment  │ │ Inventory│
│ Service  │ │ Service  │ │ Service  │
└────┬─────┘ └────┬─────┘ └────┬─────┘
     │            │            │
     └────────────┼────────────┘
                  ▼
           Единая PostgreSQL
           (orders_svc, payments_svc, inventory_svc)

Этап 3 (финал): Database-per-service (по мере готовности)
┌──────────┐ ┌──────────┐ ┌──────────┐
│  Order   │ │ Payment  │ │ Inventory│
│ Service  │ │ Service  │ │ Service  │
│  + PG-1  │ │  + PG-2  │ │  + PG-3  │
└──────────┘ └──────────┘ └──────────┘
     Saga + Outbox + CDC между ними
```

### Практические шаги

1. **Создать схемы** для новых сервисов, перенести таблицы: `ALTER TABLE ... SET SCHEMA ...`
2. **Создать роли** и выдать права на схемы
3. **Переписать запросы** в монолите на новые схемы (или создать view-алиасы)
4. **Выделить сервис** — приложение подключается под своей ролью
5. **Перевести cross-service вызовы** на API (или временно — на shared процедуры)
6. **Позже** — вынести схему в отдельную БД, перейти на Saga/Outbox

---

## 12. Сравнение: Shared DB vs Database-per-service

| Критерий | Shared Database (1 PostgreSQL) | Database-per-service (N PostgreSQL) |
|----------|-------------------------------|--------------------------------------|
| **Транзакции** | ACID (простые) | Saga / 2PC (сложные) |
| **Согласованность** | Мгновенная (strong) | Итоговая (eventual) |
| **Сложность** | Низкая | Высокая |
| **Изоляция сервисов** | Средняя (через схемы/роли) | Полная |
| **Coupling** | Высокий (общая схема) | Низкий |
| **Масштабируемость БД** | Ограничена одним инстансом | Каждый сервис масштабируется отдельно |
| **Технологии** | Все на PostgreSQL | Полиглот: Redis, Mongo, ES, etc. |
| **Независимость деплоя** | Ограниченная (миграции БД) | Полная |
| **Производительность** | Зависит от contention | Изолированная |
| **Отчётность** | Простая (JOIN) | Сложная (ETL / data warehouse) |
| **Время до маркету** | Быстрое | Медленнее |
| **Зрелость команды** | Подходит для начинающих | Требует опыта |

---

## 13. Антипаттерны

### ❌ 1. Прямой доступ к чужим таблицам без инкапсуляции

```sql
-- Order Service напрямую пишет в payments:
INSERT INTO payments_service.payments ...;
```

Проблема: Order Service знает схему Payment Service → жёсткий coupling,
любое изменение схемы payments ломает Order Service.

### ❌ 2. Одна роль для всех сервисов

```sql
-- Все сервисы подключаются как 'app_user' с полными правами
GRANT ALL ON ALL TABLES TO app_user;
```

Проблема: любой сервис может изменить любые данные, нет изоляции.

### ❌ 3. Длинные транзакции через несколько схем

```sql
BEGIN;
-- ... много шагов, долго ...
-- внешние API-вызовы внутри транзакции
PERFORM http_get('https://payment-gateway/...');
COMMIT;
```

Проблема: долгые блокировки, падение производительности, deadlocks.
Внешние вызовы внутри транзакции — особенно опасны.

### ❌ 4. Игнорирование идемпотентности

Даже при единой БД — HTTP-клиенты делают retry. Без idempotency_key —
дубликаты платежей и заказов.

### ❌ 5. Cross-schema FOREIGN KEY на «горячие» таблицы

```sql
-- FK от payments к orders:
ALTER TABLE payments_service.payments
ADD FOREIGN KEY (order_id) REFERENCES orders_service.orders(id);
```

Проблема: блокировки при INSERT/UPDATE в orders затрагивают payments.
Рекомендация: FK только на стабильные reference-данные, не на OLTP-таблицы.

### ❌ 6. Отсутствие стратегии разделения БД

Команда «застревает» на Shared DB и не планирует переход к
Database-per-service, хотя масштаб уже требует автономности.

### ❌ 7. Миграции БД без координации

Каждый сервис меняет свою схему независимо, но при единой БД миграции
могут конфликтовать (lock contention, долгие ALTER TABLE).

Решение: единый инструмент миграций (Flyway, Liquibase) с
последовательным применением, или тщательная координация.

---

## 14. Рекомендуемая архитектура

### Для единой БД PostgreSQL с микросервисами

```
                         ┌──────────────────────────────────┐
                         │        API Gateway / Client       │
                         └────────────────┬─────────────────┘
                                          │
            ┌─────────────────────────────┼─────────────────────────────┐
            │                             │                             │
     ┌──────▼──────┐              ┌───────▼───────┐             ┌───────▼───────┐
     │    Order     │              │    Payment     │             │  Inventory    │
     │   Service    │              │    Service     │             │   Service     │
     │ (orders_app) │              │(payments_app)  │             │(inventory_app)│
     └──────┬───────┘              └───────┬────────┘             └───────┬────────┘
            │                              │                              │
            │  ┌───────────────────────────▼────────────────────────────┐  │
            │  │               Единая PostgreSQL                         │  │
            │  │                                                        │  │
            │  │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐ │  │
            └─►│  │orders_service │  │payments_svc  │  │inventory_svc │ │◄─┘
               │  │(orders_app)   │  │(payments_app)│  │(inventory_app)│ │
               │  └──────────────┘  └──────────────┘  └──────────────┘ │
               │                                                        │
               │  ┌──────────────┐  ┌────────────────────────────────┐ │
               │  │shared_procs   │  │outbox (outbox_events)          │ │
               │  │(SECURITY      │  │  • orders_app   → INSERT       │ │
               │  │ DEFINER funcs)│  │  • payments_app → INSERT       │ │
               │  └──────────────┘  │  • outbox_publisher → SELECT    │ │
               │                     └────────────────────────────────┘ │
               └────────────────────────────────────────────────────────┘
                                              │
                                     ┌────────▼────────┐
                                     │  Outbox Publisher │
                                     │  (FOR UPDATE      │
                                     │   SKIP LOCKED)    │
                                     └────────┬────────┘
                                              │
                                     ┌────────▼────────┐
                                     │  Kafka/RabbitMQ  │
                                     └─────────────────┘
```

### Рекомендации

1. **Schema-per-service** — каждый сервис в своей схеме (namespace)
2. **Role-per-service** — каждый сервис под своей ролью с минимальными правами
3. **WRITE только в свою схему** — enforce через GRANT
4. **READ чужих данных через view или READ-only GRANT** — не через прямые таблицы
5. **Cross-service транзакции через SECURITY DEFINER функции** — инкапсуляция
6. **Outbox для событий** — атомарная запись + доставка в брокер
7. **FOR UPDATE SKIP LOCKED** — параллельная обработка outbox
8. **LISTEN/NOTIFY** — быстрый будильник для publisher
9. **Идемпотентность** — idempotency_key + ON CONFLICT
10. **Advisory locks** — сериализация по aggregate_id
11. **SAVEPOINT** — частичный откат при try-confirm логике
12. **Материализованные views** — для read-моделей и отчётов
13. **План разделения** — roadmap перехода к Database-per-service при росте

### Когда переходить к Database-per-service?

| Сигнал | Действие |
|--------|----------|
| БД стала узким местом (CPU, I/O) | Вынести горячие сервисы в отдельные БД |
| Команды хотят автономного деплоя | Разделить БД для ключевых сервисов |
| Нужны разные технологии (Redis, ES) | Вынести соответствующие сервисы |
| Coupling мешает изменениям | Разделить домены с сильной связанностью |
| Сильная нагрузка на одну таблицу | Шардирование или вынос в отдельную БД |

---

## 15. Глоссарий

| Термин | Определение |
|--------|-------------|
| **Shared Database** | Паттерн: несколько микросервисов работают с одной БД |
| **Schema-per-service** | Каждый сервис имеет собственную схему (namespace) в общей БД |
| **Role-per-service** | Каждый сервис подключается под отдельной PostgreSQL-ролью |
| **SECURITY DEFINER** | Функция, выполняющаяся с правами владельца, а не вызывающего |
| **ACID** | Atomicity, Consistency, Isolation, Durability |
| **Strangler Fig** | Паттерн постепенной замены монолита микросервисами |
| **Outbox** | Паттерн атомарной записи данных и события в одну транзакцию |
| **Advisory Lock** | Блокировка на уровне приложения по числовому/строковому ключу |
| **Row-Level Security** | Политики PostgreSQL, ограничивающие видимые строки по роли |
| **SAVEPOINT** | Точка внутри транзакции для частичного отката |
| **Materialized View** | Физически сохранённое представление (snapshot) данных |
| **LISTEN/NOTIFY** | Встроенный pub/sub механизм PostgreSQL |
| **FOR UPDATE SKIP LOCKED** | Блокировка строк с пропуском уже заблокированных |
| **Идемпотентность** | Повторное выполнение операции даёт тот же результат |
| **Coupling** | Степень зависимости между сервисами |
| **Database-per-service** | Канонический паттерн: каждый сервис имеет собственную БД |

---

*Исследование подготовлено для сценария единой базы данных PostgreSQL
с микросервисной архитектурой (Shared Database Pattern). Рекомендуется
как промежуточный этап при эволюции от монолита к полным микросервисам.*