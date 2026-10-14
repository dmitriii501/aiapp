# Kairo

**Открытый AI-суперапп для Windows.** Агент для кода уровня Claude Code / Codex / Cursor, управление ПК, MCP и каталог агентов — в одном нативном приложении. Подключи свою модель (BYOK) или локальную — без привязки к одному вендору.

> **Статус:** pre-alpha. Десктоп-приложение и CLI работают: агент читает, ищет и правит файлы, запускает команды, спрашивает разрешения, откатывает правки; рой агентов работает над одной задачей в отдельных git worktree; управление ПК с быстрой моделью решений проверено на живых программах. Ключи хранятся в диспетчере учётных данных Windows. Установщик не подписан; каталога агентов, индекса кодовой базы и игровой части управления ПК ещё нет. Что сделано, а что нет — в [ROADMAP](docs/ROADMAP.md) и [FEATURES](docs/FEATURES.md).

## Что умеет

- **Агент для кода** — читает и правит проект, запускает команды, пишет тесты, делает коммиты. Plan mode, сабагенты, чекпоинты с откатом правок, todo-список, хуки, скиллы, свои команды, память, поиск в интернете.
- **Рой агентов** — лид планирует, воркеры пишут код параллельно, каждый в своём git worktree и ветке; общая доска задач, сообщения, бюджеты токенов и времени, пауза и продолжение. Команды воркеров идут **в песочнице** (AppContainer) по умолчанию, git для них только на чтение. Раздел «Рой» в приложении и `kairo swarm` в терминале.
- **Свои модели** — Anthropic, любой OpenAI-совместимый endpoint (OpenAI, OpenRouter, DeepSeek, Ollama, LM Studio…). Ключи в Windows Credential Manager. Prompt caching у шлюзов формата Anthropic.
- **MCP** — подключение MCP-серверов (stdio / HTTP), конфиг в формате Claude Desktop.
- **Управление ПК** — окна, приложения, UI Automation, скриншоты, мышь и клавиатура. Две модели в связке: LLM планирует (System 2), быстрая модель решений (System 1, например Jev) выбирает каждый шаг за ~0,3 с и работает, пока LLM думает. Рискованные шаги она передаёт LLM и пользователю, остановка клавишей Pause, красная плашка поверх экрана. Подробно — в [спецификации](docs/specs/pc-control.md).
- **Привычный интерфейс** — устроен как Claude Code, оформлен как классический редактор кода: сессии, строка состояния, светлая и тёмная темы, встроенный терминал.
- **CLI** — тот же движок из терминала (`kairo`), для скриптов и CI.

**Пока нет:** каталог агентов и роёв, индекс кодовой базы, inline-правка, игры (быстрая часть, работающая по кадрам), подпись установщика, английский интерфейс.

**Стек:** C# / .NET 10 (ядро, система) + WinUI 3 (оболочка) + React / TypeScript в WebView2 (интерфейс). Почему так — в [ADR-0001](docs/adr/0001-tech-stack.md).

Полный список и приоритеты — в [docs/FEATURES.md](docs/FEATURES.md).

## Документация

| Документ | О чём |
|---|---|
| [VISION](docs/VISION.md) | Зачем проект, для кого, принципы, чего мы НЕ делаем |
| [FEATURES](docs/FEATURES.md) | Карта возможностей, приоритеты и статус каждой |
| [ARCHITECTURE](docs/ARCHITECTURE.md) | Компоненты, агентный цикл, провайдеры, инструменты, хранение |
| [SECURITY-MODEL](docs/SECURITY-MODEL.md) | Разрешения, песочница, управление ПК, секреты — критично для агента с доступом к ПК |
| [ROADMAP](docs/ROADMAP.md) | Этапы от MVP до 1.0 и что из них готово |
| [RELEASING](docs/RELEASING.md) | Как собрать релиз и обновления |
| [Рой агентов](docs/specs/swarm.md) | Как работает рой: доска задач, worktree, песочница, безопасность |
| [Управление ПК](docs/specs/pc-control.md) | Связка System 1 + System 2, цикл, политики, замеры |
| [Политики решений](docs/specs/decision-policy.md) | Принцип «LLM компилирует — System 1 исполняет», формат JSON-политики |
| [Agent manifest](docs/specs/agent-manifest.md) | Формат пакета агента для каталога (пока только спецификация) |
| [ADR](docs/adr/) | Архитектурные решения и их обоснование |
| [OPEN-QUESTIONS](docs/OPEN-QUESTIONS.md) | Что ещё не решено |

## Сборка

Нужны: Windows 10 22H2+ / 11, [.NET 10 SDK](https://dotnet.microsoft.com/download), Node.js 24+.

```bash
dotnet build Kairo.slnx
```

```bash
dotnet test Kairo.slnx
```

Десктоп: `src/Kairo.Desktop/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Kairo.exe`. Установщик собирается по [RELEASING](docs/RELEASING.md).

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

Рой из терминала (папка должна быть git-репозиторием с коммитом):

```bash
dotnet run --project src/Kairo.Cli -- swarm "Добавь две страницы и тесты" --workers 3 --max-tokens 400000
```

Что запускается внутри песочницы на этом ПК:

```bash
dotnet run --project src/Kairo.Cli -- sandbox check
```

## Настройки

Настройки и правила разрешений — в `%APPDATA%\Kairo\config.toml` и `.kairo\config.toml` в проекте. Всё, что даёт агенту доступ к системе (песочница, управление ПК, хуки, адрес обновлений, ключ Jev), берётся **только из пользовательского файла**: репозиторий не может это включить или ослабить.

```toml
model = "claude-opus-5-5"

[permissions]
mode = "default"                       # default | acceptEdits | plan
allow = ["Shell(dotnet test:*)", "Shell(git status)"]
deny = ["Shell(git push:*)"]

[sandbox]
shell = "workers"                      # workers (по умолчанию) | off | autonomous | always
network = false                        # сеть для команд в песочнице
read = ["C:/Users/me/AppData/Local/Programs/Python/Python312"]

[pc]                                   # управление ПК выключено, пока не включено здесь
enabled = true
apps = ["notepad.exe"]                 # или all_apps = true
vision = false                         # скриншоты и клики по координатам (модель должна читать картинки)
autonomous = false                     # true: в режиме «Без подтверждений» не спрашивать
```

## Участие

См. [CONTRIBUTING.md](CONTRIBUTING.md).

## Лицензия

[Apache License 2.0](LICENSE).
