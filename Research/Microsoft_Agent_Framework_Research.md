# Исследование: Microsoft Agent Framework и сравнение с другими AI Agent Frameworks

## Оглавление
1. [Введение в AI Agent Frameworks](#1-введение-в-ai-agent-frameworks)
2. [Microsoft Agent Framework: обзор](#2-microsoft-agent-framework-обзор)
3. [AutoGen — многогентный диалоговый фреймворк](#3-autogen--многогентный-диалоговый-фреймворк)
4. [Semantic Kernel — оркестрация и интеграция](#4-semantic-kernel--оркестрация-и-интеграция)
5. [Архитектура Microsoft Agent Framework](#5-архитектура-microsoft-agent-framework)
6. [Возможности и функции Microsoft Agent Framework](#6-возможности-и-функции-microsoft-agent-framework)
7. [LangChain и LangGraph](#7-langchain-и-langgraph)
8. [CrewAI](#8-crewai)
9. [OpenAI Agents SDK](#9-openai-agents-sdk)
10. [LlamaIndex и LlamaAgents](#10-llamaindex-и-llamaagents)
11. [Google Agent Development Kit (ADK)](#11-google-agent-development-kit-adk)
12. [Другие фреймворки](#12-другие-фреймворки)
13. [Сравнительная таблица](#13-сравнительная-таблица)
14. [Сравнение по ключевым критериям](#14-сравнение-по-ключевым-критериям)
15. [Рекомендации по выбору](#15-рекомендации-по-выбору)
16. [Глоссарий](#16-глоссарий)

---

## 1. Введение в AI Agent Frameworks

### Что такое AI Agent Framework?

**AI Agent Framework** — это программный каркас для создания автономных программных
агентов, которые используют большие языковые модели (LLM) для:
- **Понимания** запросов на естественном языке
- **Рассуждения** и планирования (reasoning, planning)
- **Выполнения действий** через инструменты (tools/functions)
- **Взаимодействия** с другими агентами и системами
- **Сохранения контекста** (memory, state)

### Эволюция AI-агентов

```
2022                    2023                     2024                     2025
  │                       │                        │                        │
  ▼                       ▼                        ▼                        ▼
LLM Chat             LangChain              Multi-Agent              Enterprise
(ChatGPT)           (chains, tools)        (AutoGen, CrewAI)       Agent Platforms
                     │                      │                        │
                     ▼                      ▼                        ▼
                   Single agent          Agent swarms          Microsoft Agent
                   Tool calling          Workflows              Framework
                                             │                        │
                                             ▼                        ▼
                                         LangGraph             Unified enterprise
                                         (graph-based)          agent framework
```

### Зачем нужен Agent Framework?

| Без фреймворка | С фреймворком |
|----------------|---------------|
| Ручная работа с API LLM | Стандартизированные абстракции |
| Самостоятельная реализация tool calling | Встроенная поддержка функций |
| Нет управления состоянием | Memory, context, checkpointing |
| Сложно создавать multi-agent | Готовые паттерны взаимодействия |
| Нет observability | Трассировка, логирование, метрики |
| Каждый проект с нуля | Переиспользуемые компоненты |

---

## 2. Microsoft Agent Framework: обзор

### Что это?

**Microsoft Agent Framework** — это унифицированный фреймворк от Microsoft для
построения AI-агентов и мульти-агентных систем, **объединяющий** два ранее
отдельных проекта:

- **AutoGen** — мульти-агентный диалоговый фреймворк (research → production)
- **Semantic Kernel** — SDK для оркестрации LLM, плагинов и инструментов

Microsoft объявила о слиянии и унификации этих проектов в 2024-2025 годах,
создав единый фреймворк, который:

1. **Объединяет** мощь мульти-агентных диалогов (AutoGen) с продакшн-готовой
   оркестрацией (Semantic Kernel)
2. **Поддерживает .NET и Python** — два основных языка
3. **Интегрирован с Azure** — Azure AI Foundry, Azure OpenAI, Azure Monitor
4. **Enterprise-ready** — безопасность, масштабируемость, observability
5. **Совместим с Model Context Protocol (MCP)** — открытым стандартом для
   подключения инструментов

### Позиционирование

```
┌─────────────────────────────────────────────────────────────┐
│              Microsoft Agent Framework                       │
│                                                              │
│  ┌──────────────────┐       ┌──────────────────────────┐   │
│  │     AutoGen       │       │    Semantic Kernel        │   │
│  │  (multi-agent     │       │  (orchestration,          │   │
│  │   conversations,  │ ────► │   plugins, memory,        │   │
│  │   workflows)      │       │   planning)               │   │
│  └──────────────────┘       └──────────────────────────┘   │
│                        │                                    │
│                        ▼                                    │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  Unified Agent Abstraction                            │  │
│  │  • Agent ↔ Agent communication                       │  │
│  │  • Tool / Function calling                           │  │
│  │  • Memory & State management                         │  │
│  │  • Workflows (sequential, parallel, graph)            │  │
│  │  • Observability (tracing, metrics)                   │  │
│  └──────────────────────────────────────────────────────┘  │
│                        │                                    │
│                        ▼                                    │
│  ┌──────────────────────────────────────────────────────┐  │
│  │  Azure Integration                                    │  │
│  │  • Azure AI Foundry (models, deployment)             │  │
│  │  • Azure OpenAI Service                               │  │
│  │  • Azure Monitor / Application Insights              │  │
│  │  • Azure AI Search (RAG)                             │  │
│  │  • MCP (Model Context Protocol)                      │  │
│  └──────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

---

## 3. AutoGen — многогентный диалоговый фреймворк

### История

**AutoGen** был разработан Microsoft Research (группа FLAME) и открыт в 2023 году.
Стал одним из первых популярных фреймворков для мульти-агентных систем.

- **AutoGen 0.2** — оригинальная версия (conversational agents, group chat)
- **AutoGen 0.4** —重大переписан с архитектурой на основе событий (event-driven),
  asynchronous API, modular design

### Ключевые концепции AutoGen

| Концепция | Описание |
|-----------|----------|
| **ConversableAgent** | Агент, способный вести диалог с другими агентами |
| **AssistantAgent** | Агент на основе LLM (выполняет рассуждения и tool calling) |
| **UserProxyAgent** | Агент-прокси для человека (выполняет код, ввод от пользователя) |
| **GroupChat** | Управляет диалогом нескольких агентов |
| **GroupChatManager** | Координатор, определяющий кто говорит следующим |
| **Code Executor** | Безопасное выполнение сгенерированного кода (Docker, local) |

### Архитектура AutoGen 0.4 (event-driven)

```
┌─────────────────────────────────────────────────────┐
│                  AutoGen Runtime                     │
│                                                      │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐          │
│  │ Agent A  │  │ Agent B  │  │ Agent C  │          │
│  │(Assistant)│ │(Assistant)│ │(UserProxy)│          │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘          │
│       │              │              │                 │
│       ▼              ▼              ▼                 │
│  ┌──────────────────────────────────────┐           │
│  │       Message Bus / Event Router       │           │
│  │  (асинхронная передача сообщений)      │           │
│  └──────────────────────────────────────┘           │
│                      │                               │
│  ┌───────────────────▼───────────────────┐          │
│  │  Tools / Code Executors / Models        │          │
│  └───────────────────────────────────────┘          │
└─────────────────────────────────────────────────────┘
```

### Пример: мульти-агентная система (Python)

```python
import asyncio
from autogen_agentchat.agents import AssistantAgent, UserProxyAgent
from autogen_agentchat.teams import RoundRobinGroupChat
from autogen_ext.models.openai import OpenAIChatCompletionClient

# Модель
model = OpenAIChatCompletionClient(model="gpt-4o")

# Агент-писатель
writer = AssistantAgent(
    name="Writer",
    model_client=model,
    system_message="You are a technical writer. Write clear documentation."
)

# Агент-критик
critic = AssistantAgent(
    name="Critic",
    model_client=model,
    system_message="You review documentation and suggest improvements."
)

# Команда: round-robin对话
team = RoundRobinGroupChat(
    participants=[writer, critic],
    max_turns=6
)

# Запуск
result = asyncio.run(team.run(task="Write docs for a REST API authentication endpoint"))
print(result)
```

### Ключевые возможности AutoGen

- ✅ **Мульти-агентные диалоги** — несколько агентов сотрудничают
- ✅ **Гибкие паттерны общения** — round-robin, selector, random
- ✅ **Code Execution** — безопасное выполнение кода в Docker
- ✅ **Tool Calling** — функции, веб-поиск, API
- ✅ **Asynchronous** — полностью async API (AutoGen 0.4+)
- ✅ **Modular** — расширяемая архитектура
- ✅ **.NET и Python** — поддержка обоих языков

---

## 4. Semantic Kernel — оркестрация и интеграция

### История

**Semantic Kernel (SK)** — SDK от Microsoft, открыт в 2023 году. Изначально
создан для интеграции LLM в приложения на .NET, позже добавлена поддержка Python и Java.

### Ключевые концепции Semantic Kernel

| Концепция | Описание |
|-----------|----------|
| **Kernel** | Центральный объект — контейнер для сервисов, плагинов, памяти |
| **Plugin** | Набор функций (native + semantic), доступных агенту |
| **Native Function** | Функция на C#/Python, вызываемая LLM через tool calling |
| **Semantic Function** | Промпт-шаблон, выполняемый LLM |
| **Planner** | Компонент для планирования последовательности вызовов |
| **Memory** | Хранилище контекста (volatile, persistent, vector) |
| **Connector** | Интеграция с внешними сервисами (Azure AI Search, Qdrant, etc.) |

### Архитектура Semantic Kernel

```
                    ┌──────────────────┐
                    │     Kernel        │
                    │  (orchestrator)   │
                    └────────┬─────────┘
                             │
          ┌──────────────────┼──────────────────┐
          │                  │                  │
   ┌──────▼──────┐  ┌───────▼───────┐  ┌───────▼───────┐
   │   Plugins    │  │   AI Services  │  │    Memory     │
   │              │  │                │  │               │
   │ • Native     │  │ • Chat LLM     │  │ • Volatile    │
   │   Functions  │  │ • Embeddings   │  │ • Persistent  │
   │ • Semantic   │  │ • Text→Image   │  │ • Vector DB   │
   │   Functions  │  │ • Audio        │  │               │
   └──────────────┘  └────────────────┘  └───────────────┘
          │                  │
          ▼                  ▼
   ┌──────────────┐  ┌────────────────┐
   │  External    │  │  Planners      │
   │  APIs/Tools  │  │  (Function     │
   │  (MCP)       │  │   Calling)     │
   └──────────────┘  └────────────────┘
```

### Пример: Semantic Kernel (.NET / C#)

```csharp
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.OpenAI;

// Создание kernel
var builder = Kernel.CreateBuilder();
builder.AddAzureOpenAIChatCompletion(
    deploymentName: "gpt-4o",
    endpoint: "https://my-aoai.openai.azure.com/",
    apiKey: "...");
builder.Plugins.AddFromType<TimePlugin>();
builder.Plugins.AddFromType<WeatherPlugin>();

var kernel = builder.Build();

// Создание агента
var agent = new ChatCompletionAgent
{
    Name = "Assistant",
    Instructions = "You are a helpful assistant. Use tools when needed.",
    Kernel = kernel,
    Arguments = new KernelArguments(
        new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        })
};

// Использование
var response = await agent.InvokeAsync(
    "What's the weather in Seattle and what time is it?");
await foreach (var chunk in response)
{
    Console.Write(chunk.Content);
}
```

### Пример: Semantic Kernel (Python)

```python
from semantic_kernel import Kernel
from semantic_kernel.agents import ChatCompletionAgent
from semantic_kernel.connectors.ai.open_ai import AzureChatCompletion

# Kernel
kernel = Kernel()
kernel.add_service(AzureChatCompletion(
    deployment_name="gpt-4o",
    endpoint="https://my-aoai.openai.azure.com/",
    api_key="..."
))

# Плагин
from semantic_kernel.functions import kernel_function

class WeatherPlugin:
    @kernel_function(description="Get weather for a city")
    def get_weather(self, city: str) -> str:
        return f"Weather in {city}: 20°C, sunny"

kernel.add_plugin(WeatherPlugin(), plugin_name="weather")

# Агент
agent = ChatCompletionAgent(
    kernel=kernel,
    name="Assistant",
    instructions="You are a helpful assistant.",
)

# Запуск
async for response in agent.invoke("What's the weather in Seattle?"):
    print(response.content)
```

### Ключевые возможности Semantic Kernel

- ✅ **Plugin-архитектура** — нативные и семантические функции
- ✅ **Planners** — автоматическое планирование последовательности
- ✅ **Memory** — векторная память, контекст, историчность
- ✅ **Multi-language** — C#, Python, Java
- ✅ **Azure-интеграция** — Azure OpenAI, Azure AI Search, Azure Monitor
- ✅ **MCP support** — Model Context Protocol для инструментов
- ✅ **Telemetry** — встроенная телеметрия (OpenTelemetry)
- ✅ **Filters / Middleware** — перехват и модификация вызовов

---

## 5. Архитектура Microsoft Agent Framework

### Унифицированная модель

Microsoft Agent Framework объединяет AutoGen и Semantic Kernel в единую
архитектуру, где:

- **Semantic Kernel** предоставляет инфраструктуру (kernel, plugins, memory,
  connectors, planners)
- **AutoGen** предоставляет мульти-агентную координацию (teams, group chat,
  agent communication)
- **Единый API** для создания агентов и управления ими

```
┌────────────────────────────────────────────────────────────────┐
│                Microsoft Agent Framework                         │
│                                                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              Agent Layer                                   │  │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐      │  │
│  │  │ ChatAgent   │  │ Assistant    │  │ Custom       │      │  │
│  │  │             │  │ Agent        │  │ Agent        │      │  │
│  │  └─────────────┘  └─────────────┘  └─────────────┘      │  │
│  └──────────────────────────────────────────────────────────┘  │
│                              │                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              Team / Orchestration Layer                    │  │
│  │  ┌───────────┐  ┌───────────┐  ┌───────────────────┐    │  │
│  │  │ Round     │  │ Selector  │  │ Graph / Workflow  │    │  │
│  │  │ Robin     │  │ Group     │  │ (custom DAG)      │    │  │
│  │  └───────────┘  └───────────┘  └───────────────────┘    │  │
│  └──────────────────────────────────────────────────────────┘  │
│                              │                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              Infrastructure Layer (Semantic Kernel)       │  │
│  │  ┌────────┐ ┌────────┐ ┌────────┐ ┌────────┐           │  │
│  │  │Plugins │ │Memory  │ │Planner │ │Models  │           │  │
│  │  │(Tools) │ │(State) │ │(Plan)  │ │(LLM)   │           │  │
│  │  └────────┘ └────────┘ └────────┘ └────────┘           │  │
│  └──────────────────────────────────────────────────────────┘  │
│                              │                                  │
│  ┌──────────────────────────────────────────────────────────┐  │
│  │              Integration Layer                             │  │
│  │  Azure AI Foundry │ Azure OpenAI │ MCP │ OpenTelemetry   │  │
│  └──────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────┘
```

### Поддерживаемые языки

| Язык | Поддержка | Пакет |
|------|-----------|-------|
| **Python** | ✅ Полная | `microsoft-agents-core`, `autogen-agentchat`, `semantic-kernel` |
| **.NET / C#** | ✅ Полная | `Microsoft.SemanticKernel`, `Microsoft.SemanticKernel.Agents` |
| **Java** | ⚠️ Частично (SK) | `semantic-kernel-java` |

### Жизненный цикл агента

```
1. Создание: Agent = ChatCompletionAgent(kernel, instructions, plugins)
       │
       ▼
2. Инвокация: agent.invoke(message) → Streaming response
       │
       ▼
3. Tool Calling: LLM → function_call → Kernel → Plugin → Result → LLM
       │
       ▼
4. Мульти-агент: team.run(task) → RoundRobin/Selector → Agents collaborate
       │
       ▼
5. Завершение: Result + History + Token usage + Trace
```

---

## 6. Возможности и функции Microsoft Agent Framework

### 6.1. Типы агентов

| Тип | Описание | Источник |
|-----|----------|----------|
| **ChatCompletionAgent** | Базовый агент на основе LLM с инструментами | SK |
| **AssistantAgent** | Агент-ассистент с system message и tool calling | AutoGen |
| **OpenAIAssistantAgent** | Агент на базе OpenAI Assistants API | AutoGen |
| **AzureAIAgent** | Агент на базе Azure AI Foundry Agent Service | SK/Azure |
| **Custom Agent** | Пользовательский агент с любой логикой | Оба |

### 6.2. Мульти-агентные паттерны

| Паттерн | Описание |
|---------|----------|
| **RoundRobin** | Агенты говорят по очереди (A→B→C→A→B→C...) |
| **Selector** | LLM-селектор выбирает, кто говорит следующим |
| **Random** | Случайный выбор говорящего |
| **Graph / Workflow** | Кастомный направленный граф взаимодействия |
| **Handoff** | Передача управления от одного агента к другому |
| **Human-in-the-loop** | Включение человека в диалог |

### 6.3. Tool Calling и Plugins

```csharp
// .NET: плагин с нативными функциями
public class SearchPlugin
{
    [KernelFunction("search_web")]
    [Description("Search the web for information")]
    public async Task<string> SearchWebAsync(string query)
    {
        // логика поиска
        return "Search results...";
    }

    [KernelFunction("search_database")]
    [Description("Search internal database")]
    public async Task<string> SearchDbAsync(string query)
    {
        // логика поиска в БД
        return "DB results...";
    }
}

// Регистрация
kernel.Plugins.AddFromType<SearchPlugin>("search");
```

```python
# Python: плагин
from semantic_kernel.functions import kernel_function

class SearchPlugin:
    @kernel_function(description="Search the web", name="search_web")
    async def search_web(self, query: str) -> str:
        return "Search results..."

    @kernel_function(description="Search database", name="search_db")
    async def search_db(self, query: str) -> str:
        return "DB results..."

kernel.add_plugin(SearchPlugin(), plugin_name="search")
```

### 6.4. Memory и State

```csharp
// .NET: векторная память с Azure AI Search
kernel.Plugins.AddFromType<MemoryPlugin>();

var memoryBuilder = new MemoryBuilder();
memoryBuilder.WithAzureAISearchMemoryStore(
    endpoint: "https://my-search.search.windows.net",
    apiKey: "...");

// Semantic Memory — сохранение и поиск по семантике
await kernel.Memory.SaveInformationAsync(
    collection: "knowledge",
    text: "PostgreSQL supports ACID transactions",
    id: "fact-001",
    description: "PostgreSQL ACID");

var results = kernel.Memory.SearchAsync(
    collection: "knowledge",
    query: "database transactions",
    limit: 5);
```

### 6.5. Observability (наблюдаемость)

```csharp
// .NET: OpenTelemetry-трассировка
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .AddSource("Microsoft.SemanticKernel")
    .AddSource("Microsoft.Agents")
    .AddConsoleExporter()
    .AddOtlpExporter()  // → Azure Monitor / Jaeger / etc.
    .Build();

// Все вызовы агентов автоматически трассируются:
// • LLM calls (model, tokens, latency)
// • Tool calls (function, args, result, duration)
// • Agent interactions (turns, messages)
```

### 6.6. MCP (Model Context Protocol) интеграция

Microsoft Agent Framework поддерживает **MCP** — открытый стандарт Anthropic
для подключения инструментов к LLM:

```csharp
// .NET: подключение MCP-сервера как плагина
var mcpPlugin = await McpPlugin.CreateAsync(
    command: "npx",
    args: new[] { "@modelcontextprotocol/server-filesystem", "/data" }
);

kernel.Plugins.Add(mcpPlugin);
// Теперь агент может вызывать MCP-инструменты (file_read, file_write, etc.)
```

```python
# Python: MCP integration
from semantic_kernel.connectors.mcp import McpPlugin

mcp_plugin = await McpPlugin.create(
    command="npx",
    args=["@modelcontextprotocol/server-filesystem", "/data"]
)
kernel.add_plugin(mcp_plugin)
```

### 6.7. Azure AI Foundry Agent Service

Microsoft Agent Framework нативно интегрирован с **Azure AI Foundry Agent Service** —
управляемым облачным сервисом для агентов:

```csharp
// .NET: агент на базе Azure AI Foundry
var agent = new AzureAIAgent(
    client: projectClient,
    definition: new AzureAIAgentDefinition(
        modelId: "gpt-4o",
        name: "CustomerSupport",
        instructions: "You are a customer support agent...")
    );

// Агент выполняется в облаке Azure с:
// • Управляемой памятью и потоками (threads)
// • Встроенными инструментами (code interpreter, file search)
// • Автоматическим масштабированием
// • Enterprise security
```

---

## 7. LangChain и LangGraph

### LangChain

**LangChain** — один из самых популярных open-source фреймворков для LLM-приложений
(создан в 2022, Python + JavaScript/TypeScript).

| Концепция | Описание |
|-----------|----------|
| **LLM/ChatModel** | Обёртка над API LLM |
| **Prompt Template** | Параметризованные шаблоны промптов |
| **Chain** | Последовательность вызовов (LLM → Parser → LLM) |
| **Tool / Tool Calling** | Функции, вызываемые LLM |
| **Agent** | LLM + Tools + Reasoning loop (ReAct) |
| **AgentExecutor** | Цикл выполнения агента |
| **Retriever** | Поиск документов для RAG |
| **Memory** | История диалога |

### LangGraph

**LangGraph** — расширение LangChain для создания **stateful, multi-actor**
приложений на основе направленных графов. Выпущен в 2024 году.

```
                    ┌──────────┐
              ┌─────│  START   │─────┐
              │     └──────────┘     │
              ▼                      ▼
        ┌──────────┐          ┌──────────┐
        │  Agent A │          │  Agent B │
        │(Research)│          │(Writing) │
        └────┬─────┘          └────┬─────┘
             │                     │
             ▼                     ▼
        ┌──────────┐          ┌──────────┐
        │  Review  │─────────►│  END     │
        │(Check)   │          └──────────┘
        └──────────┘
```

### Ключевые возможности LangGraph

- ✅ **Stateful graphs** — состояние передаётся между узлами
- ✅ **Cycles** — поддержка циклов (агент может повторять шаги)
- ✅ **Human-in-the-loop** — пауза для подтверждения человеком
- ✅ **Persistence** — checkpointing, resumable workflows
- ✅ **Streaming** — потоковая передача событий
- ✅ **Sub-graphs** — вложенные графы
- ✅ **Time travel** — возврат к предыдущим состояниям

### Пример LangGraph (Python)

```python
from langgraph.graph import StateGraph, END
from typing import TypedDict, Annotated
from langchain_openai import ChatOpenAI

class AgentState(TypedDict):
    messages: Annotated[list, "messages"]
    task: str
    result: str

llm = ChatOpenAI(model="gpt-4o")

def research_node(state: AgentState):
    response = llm.invoke(f"Research: {state['task']}")
    return {"messages": [response], "result": response.content}

def review_node(state: AgentState):
    response = llm.invoke(f"Review and improve: {state['result']}")
    return {"messages": [response], "result": response.content}

# Граф
workflow = StateGraph(AgentState)
workflow.add_node("research", research_node)
workflow.add_node("review", review_node)
workflow.set_entry_point("research")
workflow.add_edge("research", "review")
workflow.add_edge("review", END)

app = workflow.compile()
result = app.invoke({"task": "Analyze PostgreSQL transaction patterns"})
```

### LangChain + LangGraph: плюсы и минусы

**Плюсы:**
- ✅ Огромная экосистема интеграций (500+)
- ✅ Большое сообщество и документация
- ✅ Python и JavaScript/TypeScript
- ✅ LangGraph — мощный графовый движок
- ✅ LangSmith — платформа observability
- ✅ LangServe — деплой как API

**Минусы:**
- ❌ Сложный API, высокий порог входа
- ❌ Частые breaking changes между версиями
- ❌ Только Python и JS/TS (нет .NET)
- ❌ LangChain «тяжёлый» — много абстракций
- ❌ Переносимость кода между версиями проблематична

---

## 8. CrewAI

### Что это?

**CrewAI** — open-source фреймворк для создания **команд** (crews) AI-агентов,
которые сотрудничают для выполнения задач. Создан João Moura в 2023-2024.

### Ключевые концепции

| Концепция | Описание |
|-----------|----------|
| **Agent** | Агент с ролью, целью, backstory, инструментами |
| **Task** | Задача с описанием, ожидаемым результатом, назначенным агентом |
| **Crew** | Команда агентов и задач с процессом выполнения |
| **Process** | Sequential (последовательный) или Hierarchical (иерархический) |
| **Tool** | Инструмент (функция), доступный агенту |

### Пример CrewAI (Python)

```python
from crewai import Agent, Task, Crew, Process
from langchain_openai import ChatOpenAI

llm = ChatOpenAI(model="gpt-4o")

# Агенты
researcher = Agent(
    role="Senior Researcher",
    goal="Find accurate information about the topic",
    backstory="Expert researcher with 20 years of experience",
    llm=llm,
    verbose=True
)

writer = Agent(
    role="Technical Writer",
    goal="Write clear and engaging content",
    backstory="Former journalist with expertise in tech",
    llm=llm,
    verbose=True
)

# Задачи
research_task = Task(
    description="Research PostgreSQL distributed transactions",
    expected_output="A comprehensive research summary",
    agent=researcher
)

writing_task = Task(
    description="Write an article based on the research",
    expected_output="A polished article of 1000 words",
    agent=writer
)

# Команда
crew = Crew(
    agents=[researcher, writer],
    tasks=[research_task, writing_task],
    process=Process.sequential,
    verbose=True
)

# Запуск
result = crew.kickoff()
```

### Плюсы и минусы CrewAI

**Плюсы:**
- ✅ Очень простой и интуитивный API
- ✅ Концепция «ролей» и «команд» — естественно для разработчиков
- ✅ Быстрый старт — минимум кода
- ✅ Sequential и Hierarchical процессы
- ✅ Хорошая документация
- ✅ CrewAI Enterprise — облачная платформа

**Минусы:**
- ❌ Только Python
- ❌ Меньше интеграций, чем у LangChain
- ❌ Ограниченная гибкость (не графовый, как LangGraph)
- ❌ Enterprise-функции платные
- ❌ Нет встроенной observability на уровне LangSmith

---

## 9. OpenAI Agents SDK

### Что это?

**OpenAI Agents SDK** (ранее известный как **Swarm** — experimental) — официальный
SDK от OpenAI для создания мульти-агентных систем. Выпущен в 2024-2025 годах.

### Ключевые концепции

| Концепция | Описание |
|-----------|----------|
| **Agent** | Агент с инструкциями, инструментами, моделью |
| **Handoff** | Передача управления между агентами |
| **Guardrail** | Проверка входа/выхода для безопасности |
| **Runner** | Цикл выполнения агента |
| **Tool** | Функция, доступная агенту |
| **Session** | Сохранение состояния диалога |
| **Tracing** | Встроенная трассировка |

### Пример OpenAI Agents SDK (Python)

```python
from agents import Agent, Runner, function_tool

@function_tool
def get_weather(city: str) -> str:
    """Get weather for a city"""
    return f"Weather in {city}: 20°C, sunny"

# Агенты
triage_agent = Agent(
    name="Triage",
    instructions="You route requests to the right agent.",
    handoffs=[],
)

weather_agent = Agent(
    name="Weather",
    instructions="You answer weather questions.",
    tools=[get_weather],
    handoffs=[],
)

# Handoff: triage → weather
triage_agent.handoffs = [weather_agent]

# Запуск
result = Runner.run_sync(triage_agent, "What's the weather in Seattle?")
print(result.final_output)
```

### Плюсы и минусы OpenAI Agents SDK

**Плюсы:**
- ✅ Официальный продукт OpenAI — нативная поддержка GPT
- ✅ Очень простой API (минимализм)
- ✅ Handoffs — элегантный паттерн передачи управления
- ✅ Guardrails — встроенная безопасность
- ✅ Встроенная трассировка (OpenAI dashboard)
- ✅ Поддержка voice (Realtime API)

**Минусы:**
- ❌ Привязка к OpenAI (не поддерживает другие LLM напрямую)
- ❌ Только Python
- ❌ Меньше функций, чем у конкурентов
- ❌ Нет .NET / Java поддержки
- ❌ Open-weights модели — ограниченная поддержка

---

## 10. LlamaIndex и LlamaAgents

### LlamaIndex

**LlamaIndex** — фреймворк, изначально созданный для **RAG** (Retrieval-Augmented
Generation) и работы с данными. Позже добавлены агентные возможности.

| Концепция | Описание |
|-----------|----------|
| **Index** | Индекс данных (vector, keyword, tree, graph) |
| **Query Engine** | Движок запросов к данным |
| **Chat Engine** | Чат-движок с историей |
| **Agent** | LLM + Tools + RAG |
| **Tool** | Query engine, function, или другой агент |
| **Workflow** | Event-driven рабочий процесс (аналог LangGraph) |

### LlamaAgents

**LlamaAgents** — расширение LlamaIndex для мульти-агентных систем:

```python
from llama_agents import AgentWorker, AgentOrchestrator
from llama_index.llms.openai import OpenAI

# Агент-воркеры
research_worker = AgentWorker(
    name="Researcher",
    llm=OpenAI(model="gpt-4o"),
    tools=[search_tool],
)

# Оркестратор
orchestrator = AgentOrchestrator(
    workers=[research_worker, ...],
)

result = orchestrator.run("Analyze PostgreSQL transaction patterns")
```

### Плюсы и минусы LlamaIndex

**Плюсы:**
- ✅ Лучшая в классе RAG-функциональность
- ✅ Богатые коннекторы данных (50+ источников)
- ✅ Workflows — event-driven как LangGraph
- ✅ Python и TypeScript
- ✅ LlamaCloud — управляемая платформа

**Минусы:**
- ❌ Агентные функции — «вторичная» возможность (RAG — первичная)
- ❌ Только Python и TS
- ❌ Нет .NET
- ❌ Меньше мульти-агентных паттернов, чем AutoGen

---

## 11. Google Agent Development Kit (ADK)

### Что это?

**Google ADK (Agent Development Kit)** — фреймворк от Google для создания
AI-агентов, интегрированный с Google Cloud и Gemini.

### Ключевые концепции

| Концепция | Описание |
|-----------|----------|
| **Agent** | Агент с инструкциями, инструментами, моделью Gemini |
| **Session** | Управление состоянием диалога |
| **Tool** | Функция или интеграция с Google services |
| **Vertex AI Agent Builder** | Облачная платформа для агентов |
| **A2A Protocol** | Agent-to-Agent коммуникационный протокол |

### Плюсы и минусы Google ADK

**Плюсы:**
- ✅ Нативная интеграция с Gemini и Google Cloud
- ✅ Vertex AI Agent Builder — enterprise-платформа
- ✅ A2A (Agent-to-Agent) протокол
- ✅ Интеграция с Google Workspace, Search, Maps
- ✅ Python и Java

**Минусы:**
- ❌ Привязка к Google Cloud
- ❌ Меньше open-source сообщества
- ❌ Нет .NET
- ❌ Документация менее развита, чем у конкурентов

---

## 12. Другие фреймворки

### PydanticAI

**PydanticAI** — фреймворк от создателей Pydantic, ориентированный на
**type-safe** агентов с валидацией:

```python
from pydantic_ai import Agent

agent = Agent('openai:gpt-4o',
    system_prompt="You are a helpful assistant.")

@agent.tool
def get_weather(ctx, city: str) -> str:
    """Get weather for a city"""
    return f"20°C, sunny in {city}"

result = agent.run_sync("Weather in Seattle?")
```

- ✅ Type-safe, Pydantic-валидация
- ✅ Простой, минималистичный
- ✅ Python
- ❌ Новинка, меньше функций

### AutoGPT / BabyAGI

Ранние автономные агенты (2023):
- **AutoGPT** — автономный агент с целями и задачами
- **BabyAGI** — простой цикл task → execute → prioritize

- ✅ Инновационные концепции (автономность)
- ❌ Ненадёжные, галлюцинируют, трудно контролировать
- ❌ Больше research-проекты, чем фреймворки

### Smolagents (Hugging Face)

**smolagents** — лёгкий фреймворк от Hugging Face:
- Code-acting агенты (агенты пишут и выполняют код)
- Минимализм, Python
- Интеграция с Hugging Face Hub

### Haystack (deepset)

**Haystack** — фреймворк для NLP и RAG:
- Pipeline-ориентированная архитектура
- Сильная RAG-составляющая
- Python, enterprise-ready

---

## 13. Сравнительная таблица

| Критерий | MS Agent Framework | LangChain/LangGraph | CrewAI | OpenAI Agents SDK | LlamaIndex | Google ADK | PydanticAI |
|----------|-------------------|---------------------|--------|-------------------|------------|------------|------------|
| **Языки** | C#, Python, Java* | Python, JS/TS | Python | Python | Python, TS | Python, Java | Python |
| **Мульти-агент** | ✅ (AutoGen) | ✅ (LangGraph) | ✅ (Crews) | ✅ (Handoffs) | ✅ (LlamaAgents) | ✅ | ❌ (single) |
| **Графовые workflow** | ⚠️ (через AutoGen) | ✅ (LangGraph) | ❌ (Sequential/Hier) | ❌ | ✅ (Workflows) | ⚠️ | ❌ |
| **Tool Calling** | ✅ (Plugins) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **RAG** | ✅ (SK Memory) | ✅ (Retrievers) | ⚠️ (через tools) | ⚠️ (через tools) | ✅ (Best-in-class) | ✅ (Vertex AI Search) | ⚠️ |
| **Memory/State** | ✅ (SK Memory) | ✅ (Memory, Checkpointer) | ⚠️ (basic) | ✅ (Sessions) | ✅ (Memory) | ✅ (Sessions) | ⚠️ |
| **Human-in-loop** | ✅ (UserProxy) | ✅ (LangGraph) | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ❌ |
| **Code Execution** | ✅ (Docker) | ✅ (Code Interpreter) | ⚠️ | ✅ (Code Interpreter) | ✅ | ✅ | ❌ |
| **MCP Support** | ✅ (Native) | ⚠️ (через adapters) | ❌ | ⚠️ | ⚠️ | ❌ | ❌ |
| **Observability** | ✅ (OpenTelemetry, Azure Monitor) | ✅ (LangSmith) | ⚠️ (basic) | ✅ (OpenAI dashboard) | ✅ (LlamaCloud) | ✅ (Cloud Logging) | ⚠️ |
| **Cloud Platform** | ✅ Azure AI Foundry | ✅ LangSmith/LangServe | ✅ CrewAI Enterprise | ✅ OpenAI Platform | ✅ LlamaCloud | ✅ Vertex AI | ❌ |
| **LLM Agnostic** | ✅ (Any LLM) | ✅ (Any LLM) | ✅ (Any LLM) | ❌ (OpenAI only) | ✅ (Any LLM) | ⚠️ (Gemini first) | ✅ (Any LLM) |
| **Streaming** | ✅ | ✅ | ⚠️ | ✅ | ✅ | ✅ | ✅ |
| **Async** | ✅ (Full async) | ⚠️ (partial) | ⚠️ (partial) | ✅ | ✅ | ✅ | ✅ |
| **Enterprise-ready** | ✅ (Security, Azure AD) | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ✅ | ❌ |
| **Зрелость** | ✅ (SK mature + AutoGen mature) | ✅ (Mature) | ✅ (Growing) | ⚠️ (New) | ✅ (Mature RAG) | ⚠️ (New) | ⚠️ (New) |
| **Документация** | ✅ (хорошая) | ✅ (обширная) | ✅ (хорошая) | ⚠️ (развивается) | ✅ (хорошая) | ⚠️ | ✅ (лаконичная) |
| **Лицензия** | MIT | MIT | MIT | MIT | MIT | Apache 2.0 | MIT |
| **GitHub Stars** | ~35K (AutoGen) + ~22K (SK) | ~100K+ | ~25K+ | ~15K+ | ~37K+ | ~5K+ | ~5K+ |

> *Java — частичная поддержка (Semantic Kernel only)

---

## 14. Сравнение по ключевым критериям

### 14.1. Мульти-агентность

| Фреймворк | Паттерны | Гибкость |
|-----------|----------|----------|
| **MS Agent Framework** | RoundRobin, Selector, Graph, Handoff | Высокая |
| **LangGraph** | Произвольный граф, циклы, sub-graphs | Очень высокая |
| **CrewAI** | Sequential, Hierarchical | Средняя |
| **OpenAI Agents SDK** | Handoffs | Низкая (но простая) |
| **LlamaIndex** | Workflows (event-driven) | Высокая |

### 14.2. Интеграция с LLM

| Фреймворк | OpenAI | Azure OpenAI | Anthropic | Google | Local (Ollama) | Open Weights |
|-----------|--------|--------------|-----------|--------|----------------|---------------|
| **MS Agent Framework** | ✅ | ✅ (native) | ✅ | ✅ | ✅ | ✅ |
| **LangChain** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **CrewAI** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **OpenAI Agents SDK** | ✅ | ⚠️ | ❌ | ❌ | ⚠️ | ⚠️ |
| **LlamaIndex** | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| **Google ADK** | ⚠️ | ❌ | ❌ | ✅ (native) | ⚠️ | ⚠️ |

### 14.3. Enterprise-готовность

| Критерий | MS Agent Framework | LangChain | CrewAI | OpenAI | LlamaIndex | Google ADK |
|----------|-------------------|-----------|--------|--------|------------|------------|
| Безопасность | ✅ (Azure AD, RBAC) | ⚠️ | ⚠️ | ⚠️ | ⚠️ | ✅ (IAM) |
| Compliance | ✅ (Azure compliance) | ❌ | ❌ | ⚠️ | ❌ | ✅ |
| Observability | ✅ (OpenTelemetry) | ✅ (LangSmith) | ⚠️ | ✅ | ⚠️ | ✅ |
| Масштабирование | ✅ (Azure) | ⚠️ | ⚠️ | ✅ | ⚠️ | ✅ |
| SLA / Support | ✅ (Microsoft) | ⚠️ (LangSmith) | ⚠️ | ✅ | ⚠️ | ✅ |
| On-premises | ✅ (SK, local LLM) | ✅ | ✅ | ❌ | ✅ | ❌ |

### 14.4. Производительность и масштабируемость

| Фреймворк | Async | Streaming | Parallel Agents | Cloud Scaling |
|-----------|-------|-----------|-----------------|---------------|
| **MS Agent Framework** | ✅ Full | ✅ | ✅ | ✅ Azure |
| **LangGraph** | ✅ | ✅ | ✅ | ⚠️ (LangGraph Cloud) |
| **CrewAI** | ⚠️ | ⚠️ | ⚠️ | ⚠️ (Enterprise) |
| **OpenAI Agents SDK** | ✅ | ✅ | ⚠️ | ✅ (OpenAI) |
| **LlamaIndex** | ✅ | ✅ | ✅ | ✅ (LlamaCloud) |
| **Google ADK** | ✅ | ✅ | ✅ | ✅ (Vertex AI) |

### 14.5. Кривая обучения

```
Лёгкий ←───────────────────────────────────────────► Сложный

CrewAI    OpenAI SDK   PydanticAI   LlamaIndex   MS Agent   LangChain/LangGraph
  │           │            │            │           │              │
  ▼           ▼            ▼            ▼           ▼              ▼
(очень       (минима-    (type-       (RAG-first,  (два API,     (огромная
простой)     лизм)       safe)        +agents)     +integration) экосистема)
```

---

## 15. Рекомендации по выбору

### Выбор по сценарию

| Сценарий | Рекомендация | Обоснование |
|----------|-------------|-------------|
| **.NET / C# приложение** | **Microsoft Agent Framework** | Единственный с полной .NET-поддержкой |
| **Azure-инфраструктура** | **Microsoft Agent Framework** | Нативная интеграция с Azure AI Foundry |
| **Enterprise, compliance, security** | **Microsoft Agent Framework** или **Google ADK** | Azure/Google Cloud compliance |
| **Сложные multi-agent workflow** | **LangGraph** или **MS Agent Framework** | Графовые возможности, гибкость |
| **Быстрый прототип (Python)** | **CrewAI** или **OpenAI Agents SDK** | Простота, минимум кода |
| **RAG-приложение** | **LlamaIndex** | Лучшая RAG-функциональность |
| **OpenAI-only, simplicity** | **OpenAI Agents SDK** | Нативная интеграция, handoffs |
| **Google Cloud / Gemini** | **Google ADK** | Нативная интеграция |
| **Type-safe Python** | **PydanticAI** | Pydantic-валидация |
| **JavaScript/TypeScript** | **LangChain** | Лучшая JS/TS поддержка |
| **MCP-first архитектура** | **MS Agent Framework** | Нативная MCP-поддержка |
| **Local / on-prem LLM** | **MS Agent Framework**, **LangChain**, **CrewAI** | LLM-agnostic |

### Выбор по стеку технологий

```
Стек: .NET / C#
  └─► Microsoft Agent Framework (единственный полноценный выбор)

Стек: Python + Azure
  └─► Microsoft Agent Framework (Azure-интеграция)
  └─► LangChain (если нужна экосистема)

Стек: Python + OpenAI
  └─► OpenAI Agents SDK (нативно)
  └─► CrewAI (если нужна мульти-агентность)
  └─► LangChain (если нужна гибкость)

Стек: Python + Google Cloud
  └─► Google ADK (нативно)

Стек: Python + RAG
  └─► LlamaIndex (RAG-first)

Стек: JavaScript / TypeScript
  └─► LangChain (лучшая JS/TS поддержка)
```

### Microsoft Agent Framework: когда выбирать

✅ **Выбирайте Microsoft Agent Framework, если:**

1. Вы работаете в **.NET / C#** экосистеме
2. У вас **Azure-инфраструктура** (Azure OpenAI, Azure AI Search, Azure Monitor)
3. Нужны **enterprise-требования**: безопасность, compliance, SLA
4. Нужна **мульти-агентность** с продакшн-надёжностью
5. Хотите использовать **MCP** (Model Context Protocol)
6. Нужна **observability** через OpenTelemetry / Azure Monitor
7. Команда знакома с **Semantic Kernel** или **AutoGen**
8. Нужна поддержка **Azure AI Foundry Agent Service** (управляемые агенты)

❌ **Не выбирайте, если:**

1. Вы используете **JavaScript / TypeScript** как основной стек
2. Нужна максимальная **гибкость графа** (LangGraph мощнее)
3. Нужна **огромная экосистема** интеграций (LangChain больше)
4. Вы работаете **только с OpenAI** и хотите минимум абстракций
5. Нужен **быстрый прототип** с минимальным обучением (CrewAI проще)

### Тренды и будущее

| Тренд | Влияние |
|-------|---------|
| **MCP (Model Context Protocol)** | Стандартизация инструментов — MS Agent Framework впереди |
| **A2A (Agent-to-Agent)** | Google продвигает, MS и другие — следуют |
| **Structured Outputs** | Все фреймворки добавляют |
| **Voice Agents** | OpenAI Realtime, Google, MS — все развивают |
| **Agent-as-a-Service** | Azure AI Foundry, OpenAI, Google Vertex — облачные агенты |
| **Autonomous Agents** | Растёт интерес, но production-ready пока ограничен |
| **Слияние фреймворков** | MS объединил AutoGen+SK; другие могут последовать |

---

## 16. Глоссарий

| Термин | Определение |
|--------|-------------|
| **AI Agent** | Автономная программа на основе LLM, способная рассуждать и действовать |
| **Multi-Agent System** | Система из нескольких взаимодействующих агентов |
| **Tool Calling / Function Calling** | Способность LLM вызывать внешние функции |
| **RAG** | Retrieval-Augmented Generation — дополнение LLM внешними данными |
| **ReAct** | Reasoning + Acting — паттерн рассуждения и действий |
| **Handoff** | Передача управления от одного агента к другому |
| **Plugin** | Набор функций, доступных агенту (Semantic Kernel) |
| **Kernel** | Центральный объект оркестрации в Semantic Kernel |
| **MCP** | Model Context Protocol — открытый стандарт для инструментов LLM |
| **A2A** | Agent-to-Agent протокол коммуникации |
| **Checkpointing** | Сохранение состояния для восстановления |
| **Observability** | Трассировка, метрики, логи для мониторинга агентов |
| **Guardrail** | Проверка входа/выхода для безопасности |
| **Workflow** | Event-driven рабочий процесс (LangGraph, LlamaIndex) |
| **Crew** | Команда агентов в CrewAI |
| **Azure AI Foundry** | Облачная платформа Microsoft для AI-приложений |
| **LangSmith** | Платформа observability от LangChain |
| **Vertex AI** | Облачная AI-платформа Google |

---

## Источники

- [Microsoft Agent Framework — официальная документация](https://learn.microsoft.com/en-us/semantic-kernel/)
- [AutoGen — GitHub](https://github.com/microsoft/autogen)
- [Semantic Kernel — GitHub](https://github.com/microsoft/semantic-kernel)
- [LangChain — документация](https://python.langchain.com/)
- [LangGraph — документация](https://langchain-ai.github.io/langgraph/)
- [CrewAI — документация](https://docs.crewai.com/)
- [OpenAI Agents SDK — GitHub](https://github.com/openai/openai-agents)
- [LlamaIndex — документация](https://docs.llamaindex.ai/)
- [Google ADK — документация](https://google.github.io/adk-docs/)
- [PydanticAI — документация](https://ai.pydantic.dev/)
- [Model Context Protocol](https://modelcontextprotocol.io/)

---

*Исследование подготовлено на основе анализа официальной документации, GitHub-репозиториев
и лучших практик разработки AI-агентов. Информация актуальна на момент подготовки (2025 г.).*