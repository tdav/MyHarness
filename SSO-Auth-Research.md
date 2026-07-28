# SSO-аутентификация: микросервисы, Vue3-фронтенд и консольное приложение

> Подробное исследование по теме Single Sign-On (SSO) для архитектуры из
> микросервисов, SPA-фронтенда на Vue 3 и консольного приложения (.NET).
> Документ основан на актуальных стандартах IETF/OpenID Foundation и
> официальной документации Microsoft. Источники приведены в конце.

---

## Оглавление

1. [Базовые понятия и стандарты](#1-базовые-понятия-и-стандарты)
2. [Архитектура SSO в системе микросервисов](#2-архитектура-sso-в-системе-микросервисов)
3. [Потоки (flows) OAuth 2.0 / OIDC — какой и когда](#3-потоки-flows-oauth-20--oidc--какой-и-когда)
4. [Фронтенд: Vue 3 SPA + OIDC/PKCE](#4-фронтенд-vue-3-spa--oidcpkce)
5. [Консольное приложение (.NET) + SSO](#5-консольное-приложение-net--sso)
6. [Межсервисная аутентификация (service-to-service)](#6-межсервисная-аутентификация-service-to-service)
7. [Валидация токенов на стороне ресурсных сервисов](#7-валидация-токенов-на-стороне-ресурсных-сервисов)
8. [Безопасность: чек-лист по RFC 9700 (OAuth 2.0 Security BCP, январь 2025)](#8-безопасность-чек-лист-по-rfc-9700-oauth-20-security-bcp-январь-2025)
9. [Рекомендуемый стек технологий](#9-рекомендуемый-стек-технологий)
10. [Источники](#10-источники)

---

## 1. Базовые понятия и стандарты

### 1.1. Что такое SSO

**Single Sign-On (SSO)** — механизм, при котором пользователь один раз
проходит аутентификацию у центрального **провайдера удостоверений
(Identity Provider, IdP)** и получает доступ ко всем связанным
приложениям без повторного ввода учётных данных. В современных системах
SSO строится на базе **OAuth 2.0** + **OpenID Connect (OIDC)**.

### 1.2. Ключевые стандарты

| Стандарт | RFC / документ | Назначение |
|---|---|---|
| OAuth 2.0 Authorization Framework | RFC 6749 (2012) | Базовый фреймворк авторизации |
| OAuth 2.0 Bearer Token Usage | RFC 6750 | Использование bearer-токенов |
| JWT (JSON Web Token) | RFC 7519 (2015) | Формат токенов с claims |
| PKCE | RFC 7636 (2015) | Защита authorization code для публичных клиентов |
| Token Introspection | RFC 7662 (2015) | Проверка токена через introspection endpoint |
| OAuth 2.0 for Native Apps | RFC 8252 (2017) | Best practice для нативных/консольных приложений |
| Authorization Server Metadata | RFC 8414 (2018) | Discovery конфигурации IdP через `.well-known` |
| OAuth 2.0 Token Exchange | RFC 8693 (2020) | Обмен токенов (STS, impersonation, delegation) |
| OAuth 2.0 Security BCP | RFC 9700 (январь 2025) | Актуальные best practices безопасности |
| OpenID Connect Core 1.0 | OIDF (2023, errata set 2) | Identity-слой поверх OAuth 2.0 |

### 1.3. Роли (по RFC 6749)

- **Resource Owner (RO)** — конечный пользователь.
- **Client** — приложение, запрашивающее доступ (Vue SPA, консольное
  приложение, микросервис).
- **Authorization Server (AS) / OpenID Provider (OP)** — IdP, выпускает
  токены (Keycloak, IdentityServer, Entra ID, Auth0, Okta).
- **Resource Server (RS)** — API/микросервис, защищающий ресурсы и
  принимающий токены.

### 1.4. Типы токенов

- **Access Token** — краткосрочный (обычно 5–60 мин), предъявляется в
  `Authorization: Bearer <token>` при вызове API. Может быть opaque
  (непрозрачный) или JWT.
- **Refresh Token** — долгоживущий, используется для получения новых
  access-токенов без повторного интерактивного входа.
- **ID Token (OIDC)** — JWT с информацией о пользователе (claims: `sub`,
  `email`, `name` и т.д.). Предназначен **только для клиента**, не для API.

### 1.5. Типы клиентов

- **Confidential client** — имеет безопасное хранилище секрета
  (`client_secret`), работает на сервере (backend-сервис, daemon).
- **Public client** — не может хранить секрет (SPA, нативное/консольное
  приложение). Использует **PKCE** вместо секрета.

---

## 2. Архитектура SSO в системе микросервисов

### 2.1. Общая схема

```
┌─────────────────────────────────────────────────────────────────┐
│                        Identity Provider (IdP)                   │
│  Keycloak / IdentityServer / Entra ID / Auth0                   │
│  ┌──────────────────┐  ┌──────────────┐  ┌───────────────────┐  │
│  │ Authorization    │  │ Token        │  │ UserInfo /        │  │
│  │ Endpoint         │  │ Endpoint     │  │ Introspection     │  │
│  └──────────────────┘  └──────────────┘  └───────────────────┘  │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ /.well-known/openid-configuration  (RFC 8414 discovery) │   │
│  └──────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────┘
        ▲                    ▲                    ▲
        │ (1) login           │ (3) token          │ (5) validate
        │                     │                    │
┌───────┴───────┐    ┌───────┴───────┐    ┌───────┴───────────┐
│  Vue 3 SPA    │    │ API Gateway /  │    │ Microservice A   │
│ (public       │───▶│ BFF (optional) │───▶│ Microservice B   │
│  client, PKCE)│    │ (confidential) │    │ (resource servers)│
└───────────────┘    └────────────────┘    └──────────────────┘
                           ▲
                           │
                   ┌───────┴───────┐
                   │ Console App   │
                   │ (.NET, public │
                   │  client)      │
                   └───────────────┘
```

### 2.2. Два архитектурных подхода

#### Подход A: SPA напрямую к IdP (рекомендуется для простых систем)

Vue SPA регистрируется как **public client** в IdP, использует
Authorization Code + PKCE. Токены хранятся в памяти (или sessionStorage),
refresh через silent renew (iframe). SPA напрямую вызывает микросервисы
с access-токеном в заголовке.

**Плюсы:** простота, меньше инфраструктуры.
**Минусы:** токены в браузере (риск XSS), сложнее с refresh-токеном.

#### Подход B: BFF (Backend-For-Frontend) / OAuth 2.0 for Browser-Based Apps

Vue SPA общается только со своим backend (BFF). BFF — **confidential
client**, хранит токены в серверной сессии (httpOnly cookie), сам
выполняет OAuth-поток. SPA получает доступ через сессионную cookie.

**Плюсы:** токены не попадают в браузер, защита от XSS, проще refresh.
**Минусы:** нужен дополнительный backend-компонент.

> **Рекомендация BCP (RFC 9700, §4.17):** для браузерных приложений
> предпочтителен BFF-подход — токены не должны покидать серверную часть.

---

## 3. Потоки (flows) OAuth 2.0 / OIDC — какой и когда

| Flow | Тип клиента | Сценарий | RFC |
|---|---|---|---|
| **Authorization Code + PKCE** | Public (SPA, native) | Веб/мобильный вход пользователя | 6749 + 7636 |
| **Client Credentials** | Confidential | Service-to-service без пользователя | 6749 §4.4 |
| **On-Behalf-Of (OBO)** | Confidential | Сервис вызывает другой сервис от имени пользователя | расширение OAuth |
| **Token Exchange (RFC 8693)** | Confidential | Обмен токена на токен для другого audience | 8693 |
| **Device Code Flow** | Public (ограниченный UI) | Консольное приложение без браузера | 8252, OAuth |
| **Resource Owner Password Credentials (ROPC)** | — | **DEPRECATED** (RFC 9700) | 6749 §4.3 |
| **Implicit Grant** | — | **DEPRECATED** (RFC 9700) | 6749 §4.2 |

> ⚠️ **RFC 9700 (январь 2025)** прямо **не рекомендует** Implicit Grant и
> ROPC. Единственный рекомендованный интерактивный поток —
> **Authorization Code + PKCE**.

---

## 4. Фронтенд: Vue 3 SPA + OIDC/PKCE

### 4.1. Рекомендуемая библиотека: `oidc-client-ts`

[`oidc-client-ts`](https://github.com/authts/oidc-client-ts) — основная
TypeScript-библиотека для OIDC/OAuth2 в браузере (наследник
`oidc-client-js` от IdentityModel). Поддерживает:

- Authorization Code Grant с **PKCE** (обязательно);
- Refresh Token Grant;
- Silent Refresh через iframe;
- Управление сессией пользователя.

> Implicit Grant **не поддерживается** — библиотека ориентируется на
> OAuth 2.1.

**Установка:**
```bash
npm install oidc-client-ts
```

### 4.2. Конфигурация UserManager

```typescript
// src/auth/oidc.ts
import { UserManager, WebStorageStateStore } from 'oidc-client-ts';

export const userManager = new UserManager({
  authority: 'https://idp.example.com/realms/myrealm',  // IdP issuer
  client_id: 'vue-spa-client',
  redirect_uri: window.location.origin + '/callback',
  post_logout_redirect_uri: window.location.origin,
  response_type: 'code',               // Authorization Code flow
  scope: 'openid profile email api.read api.write',
  // PKCE включён по умолчанию в oidc-client-ts
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  automaticSilentRenew: true,
  silent_redirect_uri: window.location.origin + '/silent-renew.html',
  loadUserInfo: true,                   // запрос к UserInfo endpoint
});
```

### 4.3. Pinia store для состояния аутентификации

```typescript
// src/stores/auth.ts
import { defineStore } from 'pinia';
import { userManager } from '@/auth/oidc';
import type { User } from 'oidc-client-ts';

export const useAuthStore = defineStore('auth', {
  state: () => ({
    user: null as User | null,
    ready: false,
  }),
  getters: {
    isAuthenticated: (s) => !!s.user && !s.user.expired,
    accessToken: (s) => s.user?.access_token ?? null,
  },
  actions: {
    async init() {
      try {
        this.user = await userManager.getUser();
      } finally {
        this.ready = true;
      }
    },
    async login() {
      await userManager.signinRedirect();
    },
    async handleCallback() {
      this.user = await userManager.signinRedirectCallback();
    },
    async logout() {
      await userManager.signoutRedirect();
    },
    async refreshToken() {
      this.user = await userManager.signinSilent();
    },
  },
});
```

### 4.4. Vue Router — защита маршрутов (navigation guards)

```typescript
// src/router/index.ts
import { createRouter, createWebHistory } from 'vue-router';
import { useAuthStore } from '@/stores/auth';

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', component: () => import('@/views/Home.vue') },
    { path: '/callback', component: () => import('@/views/Callback.vue') },
    {
      path: '/dashboard',
      component: () => import('@/views/Dashboard.vue'),
      meta: { requiresAuth: true },
    },
  ],
});

router.beforeEach(async (to) => {
  const auth = useAuthStore();
  if (!auth.ready) await auth.init();

  if (to.meta.requiresAuth && !auth.isAuthenticated) {
    await auth.login();   // редирект в IdP
    return false;
  }
});

export default router;
```

### 4.5. Callback-страница

```vue
<!-- src/views/Callback.vue -->
<script setup lang="ts">
import { onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { useAuthStore } from '@/stores/auth';

const router = useRouter();
const auth = useAuthStore();

onMounted(async () => {
  try {
    await auth.handleCallback();
    router.push('/dashboard');
  } catch (e) {
    console.error('OIDC callback error', e);
    router.push('/');
  }
});
</script>

<template><p>Обработка входа…</p></template>
```

### 4.6. Axios-интерцептор: добавление access-токена

```typescript
// src/api/client.ts
import axios from 'axios';
import { useAuthStore } from '@/stores/auth';

const api = axios.create({ baseURL: '/api' });

api.interceptors.request.use(async (config) => {
  const auth = useAuthStore();
  if (auth.isAuthenticated) {
    config.headers.Authorization = `Bearer ${auth.accessToken}`;
  }
  return config;
});

// Автоматический refresh при 401
api.interceptors.response.use(
  (r) => r,
  async (error) => {
    if (error.response?.status === 401) {
      const auth = useAuthStore();
      await auth.refreshToken();
      error.config.headers.Authorization = `Bearer ${auth.accessToken}`;
      return api.request(error.config);
    }
    return Promise.reject(error);
  },
);

export default api;
```

### 4.7. Хранение токенов — безопасность

| Хранилище | XSS-риск | Рекомендация |
|---|---|---|
| `localStorage` | Высокий (токен доступен любому JS) | ❌ Не рекомендуется |
| `sessionStorage` | Высокий | ⚠️ Только если нет BFF |
| В памяти (Pinia state) | Средний (теряется при reload) | ✅ + silent renew |
| **BFF + httpOnly cookie** | Низкий | ✅✅ Лучший вариант |

> **Правило:** access-токен не должен жить в браузере дольше необходимого.
> При BFF-подходе токены хранятся на сервере, SPA работает через
> сессионную cookie.

### 4.8. Silent Renew (обновление без перезагрузки)

`oidc-client-ts` с `automaticSilentRenew: true` открывает скрытый
iframe с `prompt=none` к IdP. Если сессия пользователя ещё активна в IdP,
библиотека получает новый access-токен автоматически. Это и есть
**суть SSO** — пользователь не логинится повторно.

---

## 5. Консольное приложение (.NET) + SSO

### 5.1. Рекомендуемая библиотека: MSAL.NET

[MSAL.NET](https://learn.microsoft.com/en-us/entra/msal/dotnet/)
(`Microsoft.Identity.Client`) — официальная библиотека Microsoft для
токенов Entra ID / Azure AD. Для других IdP (Keycloak, IdentityServer)
можно использовать `IdentityModel` / `OpenIddict`-клиент или raw HTTP.

### 5.2. Типы потоков для консольного приложения

| Сценарий | Flow | Описание |
|---|---|---|
| Интерактивный вход (есть браузер) | **Authorization Code + PKCE** (interactive) | Открывается системный браузер, redirect на loopback `http://localhost:port` |
| Без браузера / headless | **Device Code Flow** | Пользователь открывает URL на другом устройстве, вводит код |
| Сервис/daemon без пользователя | **Client Credentials** | `client_id` + `client_secret`/сертификат |
| Тихий повторный вход | **Silent** (из кэша токенов) | `AcquireTokenSilent` |

> **RFC 8252 (OAuth 2.0 for Native Apps):** нативные приложения **MUST**
> использовать внешний браузер (не embedded WebView). Для .NET-консоли
> на Windows используется системный браузер с redirect на loopback.

### 5.3. Пример: Interactive flow (Authorization Code + PKCE)

```csharp
using Microsoft.Identity.Client;

var app = PublicClientApplicationBuilder
    .Create("your-client-id")           // public client (no secret)
    .WithAuthority(new Uri("https://idp.example.com/realms/myrealm"))
    .WithRedirectUri("http://localhost") // loopback, RFC 8252 §7.3
    .Build();

// Кэш токенов (в памяти; для персистентности — файловый/зашифрованный)
var tokenCachePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MyConsoleApp", "token-cache.bin");
// TODO: реализовать сериализацию ITokenCache в файл (DPAPI/зашифрованный)

string[] scopes = new[] { "api.read", "api.write" };

AuthenticationResult result;
try
{
    // 1. Попытка тихого получения из кэша
    var accounts = await app.GetAccountsAsync();
    result = await app.AcquireTokenSilent(scopes, accounts.FirstOrDefault())
        .ExecuteAsync();
}
catch (MsalUiRequiredException)
{
    // 2. Интерактивный вход через системный браузер
    result = await app.AcquireTokenInteractive(scopes)
        .WithUseEmbeddedWebView(false)   // внешний браузер (RFC 8252)
        .ExecuteAsync();
}

Console.WriteLine($"Access token: {result.AccessToken[..20]}...");
Console.WriteLine($"Expires: {result.ExpiresOn}");
Console.WriteLine($"User: {result.Account.Username}");
```

### 5.4. Пример: Device Code Flow (headless / SSH / сервер без UI)

```csharp
var app = PublicClientApplicationBuilder
    .Create("your-client-id")
    .WithAuthority(new Uri("https://idp.example.com/realms/myrealm"))
    .Build();

var result = await app.AcquireTokenWithDeviceCode(
    new[] { "api.read", "api.write" },
    deviceCodeResult =>
    {
        // Выводим инструкции пользователю
        Console.WriteLine(deviceCodeResult.Message);
        // Пример: "To sign in, use a web browser to open the page
        //           https://idp.example.com/device and enter the code ABCD1234"
        return Task.FromResult(0);
    })
    .ExecuteAsync();

Console.WriteLine($"Token: {result.AccessToken}");
```

> Device Code Flow идеален для консольных утилит на серверах, в CI/CD,
> SSH-сессиях. Пользователь аутентифицируется на **любом устройстве** с
> браузером, а консоль опрашивает IdP до получения токена.

### 5.5. Пример: Client Credentials (daemon, без пользователя)

```csharp
var app = ConfidentialClientApplicationBuilder
    .Create("service-client-id")
    .WithClientSecret("your-client-secret")   // или .WithCertificate(cert)
    .WithAuthority(new Uri("https://idp.example.com/realms/myrealm"))
    .Build();

var result = await app.AcquireTokenForClient(
    new[] { "https://idp.example.com/.default" })
    .ExecuteAsync();

// result.AccessToken — токен для вызова API от имени сервиса
```

### 5.6. Кэширование токенов в консольном приложении

MSAL.NET хранит кэш в памяти по умолчанию. Для персистентности между
запусками нужно реализовать сериализацию `ITokenCache`:

- **Windows:** DPAPI-шифрование файла (`ProtectedData.Protect`).
- **Linux/macOS:** файл с правами `600` или keyring.
- Для confidential clients — можно использовать распределённый кэш
  (Redis) при высоких нагрузках.

### 5.7. Вызов API с полученным токеном

```csharp
using var http = new HttpClient();
http.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", result.AccessToken);

var response = await http.GetAsync("https://api.example.com/data");
```

---

## 6. Межсервисная аутентификация (service-to-service)

### 6.1. Сценарии

| Сценарий | Механизм | Контекст |
|---|---|---|
| Сервис → Сервис (без пользователя) | **Client Credentials** | Daemon, фоновые задачи |
| Сервис → Сервис (от имени пользователя) | **Token Exchange (RFC 8693)** или **OBO** | Пользовательский запрос проходит через цепочку сервисов |
| Внутри доверенной сети | **mTLS** + токены | Доп. защита транспортного уровня |

### 6.2. Client Credentials (нет пользователя)

Каждый микросервис регистрируется как **confidential client** в IdP.
При старте или по необходимости сервис получает access-токен через
`grant_type=client_credentials`:

```
POST /token HTTP/1.1
Host: idp.example.com
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
&client_id=order-service
&client_secret=********
&scope=inventory.read
```

**Важно:** токен содержит `aud` (audience) = целевой сервис, чтобы
только он мог его принять.

### 6.3. Token Exchange (RFC 8693) — обмен токенов

Когда запрос пользователя проходит через цепочку микросервисов
(A → B → C), каждый сервис может **обменять** полученный токен на новый
с уменьшенным scope и правильным audience:

```
POST /token HTTP/1.1
Host: idp.example.com
Authorization: Basic base64(service-b:secret)
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:token-exchange
&subject_token=<access_token_от_сервиса_A>
&subject_token_type=urn:ietf:params:oauth:token-type:access_token
&audience=service-c
&scope=inventory.read
```

**Ответ:**
```json
{
  "access_token": "<новый_токен_для_service-c>",
  "issued_token_type": "urn:ietf:params:oauth:token-type:access_token",
  "token_type": "N_A",
  "expires_in": 3600,
  "scope": "inventory.read"
}
```

> RFC 8693 определяет **impersonation** (сервис действует как
> пользователь) и **delegation** (сервис действует от своего имени, но
> по делегации пользователя — claim `act` в JWT).

### 6.4. On-Behalf-Of (OBO) flow

OBO — частный случай token exchange, стандартизированный Microsoft
(Entra ID). Сервис B, получивший токен от пользователя, обменивает его
на новый токен для вызова сервиса C:

```
POST /token HTTP/1.1
grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer
&assertion=<токен_пользователя>
&client_id=service-b
&client_secret=********
&scope=service-c/.default
```

### 6.5. Token Relay (простая передача)

Самый простой вариант: сервис A просто **передаёт** исходный access-токен
сервису B в заголовке `Authorization: Bearer`. Подходит, если:
- токен — JWT с правильным `aud` для B;
- срок жизни токена достаточен;
- нет необходимости в уменьшении привилегий.

**Минусы:** нет уменьшения scope, токен виден всем сервисам в цепочке,
сложнее аудит.

### 6.6. mTLS (mutual TLS) — дополнительная защита

Для service-to-service в доверенной сети/zero-trust:
- Каждый сервис имеет TLS-сертификат.
- IdP выпускает токены, привязанные к сертификату клиента
  (sender-constrained tokens, RFC 8705).
- Даже украденный токен нельзя использовать без сертификата.

### 6.7. API Gateway / BFF как централизованная точка

```
Vue SPA ──cookie──▶ BFF ──token──▶ Gateway ──▶ Service A
                                              ──▶ Service B
```

Gateway может:
- терминировать аутентификацию (проверять токен);
- добавлять токен в заголовок для downstream-сервисов;
- выполнять token exchange для каждого сервиса;
- логировать и применять rate-limiting.

---

## 7. Валидация токенов на стороне ресурсных сервисов

### 7.1. Два подхода

#### A. Локальная валидация JWT (рекомендуется для JWT-токенов)

Сервис скачивает JWKS (JSON Web Key Set) с IdP
(`jwks_uri` из `.well-known`) и проверяет подпись локально:

```csharp
// ASP.NET Core — JWT Bearer
builder.Services
  .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
  .AddJwtBearer(options =>
  {
      options.Authority = "https://idp.example.com/realms/myrealm";
      options.Audience = "my-service";        // проверка aud
      options.TokenValidationParameters = new()
      {
          ValidateIssuer = true,
          ValidIssuer = "https://idp.example.com/realms/myrealm",
          ValidateAudience = true,
          ValidAudience = "my-service",
          ValidateLifetime = true,
          ValidateIssuerSigningKey = true,
          ClockSkew = TimeSpan.FromSeconds(30),
      };
  });
```

**Проверки (обязательные):**
1. `iss` — совпадает с ожидаемым issuer.
2. `aud` — содержит идентификатор данного сервиса.
3. `exp` / `nbf` — токен не истёк и активен.
4. Подпись — проверяется по JWKS IdP.
5. `scope` / `roles` — авторизация на уровне claims.

#### B. Introspection (RFC 7662) — для opaque-токенов

Если access-токен непрозрачный (не JWT), сервис вызывает introspection
endpoint IdP:

```
POST /introspect HTTP/1.1
Host: idp.example.com
Authorization: Basic base64(service:secret)
Content-Type: application/x-www-form-urlencoded

token=<access_token>&token_type_hint=access_token
```

**Ответ:**
```json
{
  "active": true,
  "scope": "api.read",
  "client_id": "vue-spa-client",
  "username": "user@example.com",
  "exp": 1735689600,
  "sub": "12345"
}
```

> Introspection создаёт сетевой round-trip на каждый запрос — для
> производительности используют **кэш** результатов (с TTL = `exp`).

### 7.2. Discovery через RFC 8414

Все клиенты и сервисы могут получить конфигурацию IdP автоматически:

```
GET /.well-known/oauth-authorization-server HTTP/1.1
Host: idp.example.com
```

или для OIDC:
```
GET /.well-known/openid-configuration HTTP/1.1
Host: idp.example.com/realms/myrealm
```

**Ответ содержит:**
```json
{
  "issuer": "https://idp.example.com/realms/myrealm",
  "authorization_endpoint": "https://idp.example.com/.../auth",
  "token_endpoint": "https://idp.example.com/.../token",
  "introspection_endpoint": "https://idp.example.com/.../introspect",
  "userinfo_endpoint": "https://idp.example.com/.../userinfo",
  "jwks_uri": "https://idp.example.com/.../certs",
  "scopes_supported": ["openid", "profile", "email", ...],
  "grant_types_supported": ["authorization_code", "client_credentials", ...]
}
```

---

## 8. Безопасность: чек-лист по RFC 9700 (OAuth 2.0 Security BCP, январь 2025)

### 8.1. Обязательные меры

- ✅ **Использовать только Authorization Code + PKCE** для интерактивных
  потоков. Implicit Grant **deprecated**.
- ✅ **PKCE обязателен для всех клиентов** (не только public). Использовать
  `code_challenge_method=S256`.
- ✅ **Exact string matching** для redirect_uri (кроме loopback-портов
  нативных приложений).
- ✅ **Защита от CSRF:** PKCE или `state`-параметр, привязанный к сессии.
- ✅ **Защита от mix-up атак:** использовать `iss`-параметр (RFC 9207)
  или distinct redirect URIs.
- ✅ **Sender-constrained tokens** (mTLS / DPoP) для защиты от кражи
  токенов.
- ✅ **Audience-restricted tokens:** каждый access-токен должен иметь
  конкретный `aud`, не «все API».
- ✅ **Короткий lifetime access-токена** (5–15 мин), refresh-токен —
  долгоживущий, но защищённый.
- ✅ **Не использовать ROPC** (Resource Owner Password Credentials).
- ✅ **TLS 1.2+** везде, без исключений.

### 8.2. Для SPA (Vue 3)

- ✅ Не хранить токены в `localStorage`.
- ✅ Предпочитать BFF (токены на сервере, httpOnly cookie).
- ✅ Если без BFF — токены в памяти + silent renew.
- ✅ CSP (Content Security Policy) для снижения XSS-рисков.
- ✅ Проверка `state` и `nonce` при callback.

### 8.3. Для консольного приложения

- ✅ Внешний браузер (не embedded WebView) — RFC 8252.
- ✅ Loopback redirect (`http://localhost:port`) с динамическим портом.
- ✅ Шифрование кэша токенов (DPAPI на Windows).
- ✅ Для daemon — client credentials с сертификатом (не secret-строкой).

### 8.4. Для микросервисов

- ✅ Каждый сервис проверяет `aud` токена.
- ✅ Token exchange для уменьшения привилегий при вызове downstream.
- ✅ mTLS в zero-trust-сетях.
- ✅ Кэш introspection с корректным TTL.
- ✅ Логирование `jti` (JWT ID) для аудита и обнаружения replay.

---

## 9. Рекомендуемый стек технологий

### 9.1. Identity Provider (IdP)

| IdP | Тип | Примечание |
|---|---|---|
| **Keycloak** | Open-source | Полная поддержка OIDC/OAuth2, бесплатный, популярный |
| **IdentityServer / Duende** | .NET | Нативная интеграция с ASP.NET Core |
| **OpenIddict** | .NET open-source | Лёгкая альтернатива для .NET |
| **Microsoft Entra ID** | Cloud | Для экосистемы Microsoft |
| **Auth0 / Okta** | SaaS | Быстрый старт, коммерческий |

### 9.2. Фронтенд (Vue 3)

- `oidc-client-ts` — OIDC-клиент для SPA.
- `vue-router` — navigation guards для защиты маршрутов.
- `pinia` — хранение состояния аутентификации.
- `axios` / `fetch` — HTTP-клиент с интерцептором токена.

### 9.3. Консольное приложение (.NET)

- `Microsoft.Identity.Client` (MSAL.NET) — для Entra ID.
- `IdentityModel` — для произвольных OIDC-серверов.
- `System.Net.Http` — вызов API с Bearer-токеном.

### 9.4. Микросервисы (.NET)

- `Microsoft.AspNetCore.Authentication.JwtBearer` — валидация JWT.
- `IdentityModel.AspNetCore.AccessTokenManagement` — автоматический
  client credentials / token exchange.
- `Yarp.ReverseProxy` — API Gateway с OIDC-терминацией.

---

## 10. Источники

1. **RFC 6749** — The OAuth 2.0 Authorization Framework (2012)
   https://datatracker.ietf.org/doc/html/rfc6749
2. **RFC 6750** — OAuth 2.0 Bearer Token Usage (2012)
   https://datatracker.ietf.org/doc/html/rfc6750
3. **RFC 7519** — JSON Web Token (JWT) (2015)
   https://datatracker.ietf.org/doc/html/rfc7519
4. **RFC 7636** — Proof Key for Code Exchange (PKCE) (2015)
   https://datatracker.ietf.org/doc/html/rfc7636
5. **RFC 7662** — OAuth 2.0 Token Introspection (2015)
   https://datatracker.ietf.org/doc/html/rfc7662
6. **RFC 8252** — OAuth 2.0 for Native Apps (2017)
   https://datatracker.ietf.org/doc/html/rfc8252
7. **RFC 8414** — OAuth 2.0 Authorization Server Metadata (2018)
   https://datatracker.ietf.org/doc/html/rfc8414
8. **RFC 8693** — OAuth 2.0 Token Exchange (2020)
   https://datatracker.ietf.org/doc/html/rfc8693
9. **RFC 9700** — Best Current Practice for OAuth 2.0 Security (январь 2025)
   https://datatracker.ietf.org/doc/html/rfc9700
10. **OpenID Connect Core 1.0** (errata set 2, декабрь 2023)
    https://openid.net/specs/openid-connect-core-1_0.html
11. **oidc-client-ts** — OIDC/OAuth2 библиотека для браузерных JS-приложений
    https://github.com/authts/oidc-client-ts
12. **MSAL.NET** — Token acquisition overview
    https://learn.microsoft.com/en-us/entra/msal/dotnet/acquiring-tokens/overview
13. **MSAL.NET** — AcquireTokenWithDeviceCode
    https://learn.microsoft.com/en-us/dotnet/api/microsoft.identity.client.publicclientapplication.acquiretokenwithdevicecode

---

*Документ подготовлен на основе актуальных стандартов IETF и OpenID
Foundation. Все RFC проверены по первоисточникам (datatracker.ietf.org).
Дата подготовки: 2025.*