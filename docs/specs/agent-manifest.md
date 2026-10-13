# Спецификация: манифест агента (черновик v0)

Агент в каталоге — это папка с манифестом и файлами. Никакого исполняемого кода по умолчанию: агент — это **конфигурация** (промпт + инструменты + MCP + скиллы). Скрипты допускаются только внутри скиллов и проходят обычные разрешения.

## Структура

```
my-agent/
  agent.toml        # манифест
  AGENT.md          # системный промпт (Markdown)
  icon.png          # 256×256
  skills/           # опционально, формат SKILL.md
  README.md         # описание для страницы каталога
```

## agent.toml

```toml
id          = "com.example.pr-reviewer"   # уникальный, reverse-DNS
name        = "PR Reviewer"
version     = "1.2.0"                      # semver
description = "Ревьюит изменения в ветке и оставляет комментарии"
authors     = ["Имя <mail@example.com>"]
license     = "MIT"
homepage    = "https://github.com/example/pr-reviewer"
categories  = ["code", "review"]
min_app     = "0.5.0"

[model]
# Рекомендации, не жёсткие требования. Пользователь может переопределить.
prefer      = ["claude-sonnet-*", "gpt-5*"]
requires    = ["tools"]          # tools | vision | reasoning
min_context = 100_000

[tools]
# Только эти встроенные инструменты будут доступны агенту.
builtin = ["Read", "Glob", "Grep", "Shell"]

[permissions]
# Декларация. Показывается пользователю при установке.
# Агент не может получить больше, чем здесь указано.
request = ["Shell(git diff:*)", "Shell(git log:*)", "Shell(gh pr:*)"]

[[mcp]]
name    = "github"
command = "npx"
args    = ["-y", "@modelcontextprotocol/server-github"]
env     = { GITHUB_TOKEN = "${secret:github_token}" }  # спросить у пользователя, хранить в Credential Manager
optional = false

[ui]
accent  = "#7C5CFF"
starter_prompts = [
  "Проверь текущую ветку",
  "Найди потенциальные баги в последнем коммите",
]
```

## Правила

- `id` + `version` неизменяемы после публикации.
- Секреты только через `${secret:name}` — никогда в манифесте.
- Расширение `permissions.request` или `tools.builtin` в новой версии → повторное подтверждение пользователя.

## Реестр

Публичный git-репозиторий: `agents/<id>/<version>/` или ссылка на внешний репозиторий с закреплённым коммитом. Публикация — через PR, проверка CI: валидность манифеста, лицензия, отсутствие секретов, ручное ревью разрешений.

## Открытые вопросы

- TOML или YAML? (TOML читается проще и уже используется в `swarm.toml`; YAML — ближе к frontmatter `SKILL.md`)
- Совместимость с форматом сабагентов Claude Code (`.claude/agents/*.md` с frontmatter) — поддерживать импорт?
- Подпись пакетов.
