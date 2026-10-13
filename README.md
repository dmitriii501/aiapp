# Kairo

**Открытый AI-суперапп для Windows.** Агент для кода уровня Claude Code / Codex / Cursor, управление ПК, MCP и каталог агентов — в одном нативном приложении. Подключи свою модель (BYOK) или локальную — без привязки к одному вендору.

> **Статус:** pre-alpha, этап M2. Агент работает в десктоп-приложении и в терминале: читает, ищет и правит файлы, запускает команды, спрашивает разрешения, сохраняет сессии. Ключи хранятся в диспетчере учётных данных Windows.

## Что это будет уметь

- **Агент для кода** — читает и правит проект, запускает команды, пишет тесты, делает коммиты. Plan mode, сабагенты, чекпоинты с откатом, ревью диффов.
- **Рой агентов** — десятки агентов параллельно над одной задачей: лид планирует, воркеры пишут код в своих git worktree, ревьюер проверяет. Разные модели для разных ролей, общая доска задач, живая карта роя, бюджеты.
- **Свои модели** — Anthropic, OpenAI, Google, OpenRouter, любой OpenAI-совместимый endpoint, Ollama / LM Studio. Ключи хранятся в Windows Credential Manager.
- **MCP** — подключение любых MCP-серверов (stdio / HTTP), каталог популярных серверов в один клик.
- **Управление ПК** — окна, приложения, файлы, UI Automation, скриншоты, мышь/клавиатура. Две модели в связке: LLM планирует, быстрая модель решений (System 1, например Jev) выбирает каждый шаг за доли секунды. Всё через систему разрешений.
- **Каталог агентов** — готовые агенты (промпт + инструменты + MCP + скиллы) одним манифестом. Установка в один клик, публикация через PR.
- **Привычный интерфейс** — устроен как Claude Code, оформлен как классический редактор кода: дерево сессий, строка состояния, светлая и тёмная темы, управление с клавиатуры.
- **CLI** — тот же движок доступен из терминала (`kairo`), для скриптов и CI.

**Стек:** C# / .NET 10 (ядро, система) + WinUI 3 (оболочка) + React / TypeScript в WebView2 (интерфейс). Почему так — в [ADR-0001](docs/adr/0001-tech-stack.md).

Полный список и приоритеты — в [docs/FEATURES.md](docs/FEATURES.md).

## Документация

| Документ | О чём |
|---|---|
| [VISION](docs/VISION.md) | Зачем проект, для кого, принципы, чего мы НЕ делаем |
| [FEATURES](docs/FEATURES.md) | Карта возможностей, сравнение с Claude Code / Codex / Cursor, приоритеты |
| [ARCHITECTURE](docs/ARCHITECTURE.md) | Компоненты, агентный цикл, провайдеры, инструменты, хранение |
| [SECURITY-MODEL](docs/SECURITY-MODEL.md) | Разрешения, песочница, работа с секретами — критично для агента с доступом к ПК |
| [ROADMAP](docs/ROADMAP.md) | Этапы от MVP до 1.0 |
| [Рой агентов](docs/specs/swarm.md) | Как работает рой: топологии, доска задач, `swarm.toml`, безопасность |
| [Управление ПК](docs/specs/pc-control.md) | Связка System 1 + System 2, цикл наблюдение → решение → действие → проверка |
| [Политики решений](docs/specs/decision-policy.md) | Принцип «LLM компилирует — System 1 исполняет», формат JSON-политики, пример для игры |
| [Agent manifest](docs/specs/agent-manifest.md) | Формат пакета агента для каталога |
| [ADR](docs/adr/) | Архитектурные решения и их обоснование (интерфейс — [ADR-0004](docs/adr/0004-editor-style-ui.md)) |
| [OPEN-QUESTIONS](docs/OPEN-QUESTIONS.md) | Что ещё не решено |

## Сборка

Нужны: Windows 10 22H2+ / 11, [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24+.

```bash
dotnet build Kairo.slnx
```

```bash
dotnet test Kairo.slnx
```

Десктоп: `src/Kairo.Desktop/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Kairo.exe`.

Агент в терминале (ключ в `ANTHROPIC_API_KEY`), интерактивно в текущей папке:

```bash
dotnet run --project src/Kairo.Cli
```

Одна задача без диалога — всё, что требует подтверждения, будет отклонено, если не разрешено через `--allow`:

```bash
dotnet run --project src/Kairo.Cli -- -p "Найди TODO в проекте" --allow "Shell(git status)"
```

Локальная модель через Ollama:

```bash
dotnet run --project src/Kairo.Cli -- --provider openai-compatible --base-url http://localhost:11434/v1 --model qwen3
```

Настройки и правила разрешений — в `%APPDATA%\Kairo\config.toml` и `.kairo\config.toml` в проекте:

```toml
model = "claude-opus-5-5"

[permissions]
mode = "default"                       # default | acceptEdits | plan
allow = ["Shell(dotnet test:*)", "Shell(git status)"]
deny = ["Shell(git push:*)"]
```

## Участие

См. [CONTRIBUTING.md](CONTRIBUTING.md).

## Лицензия

[Apache License 2.0](LICENSE).
