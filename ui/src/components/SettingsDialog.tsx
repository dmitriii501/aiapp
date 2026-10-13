import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { X } from "lucide-react";
import { useStore } from "../store";
import type { Mode, ProviderId } from "../bridge/protocol";
import { MODE_LABELS } from "./modes";

const EFFORTS = [
  { id: "default", label: "По умолчанию" },
  { id: "low", label: "Низкая" },
  { id: "medium", label: "Средняя" },
  { id: "high", label: "Высокая" },
  { id: "xhigh", label: "Очень высокая" },
  { id: "max", label: "Максимальная" },
];

export function SettingsDialog() {
  const settings = useStore((s) => s.settings);
  const saveSettings = useStore((s) => s.saveSettings);
  const setOpen = useStore((s) => s.setSettingsOpen);

  const [provider, setProvider] = useState<ProviderId>(settings?.provider ?? "anthropic");
  const [model, setModel] = useState(settings?.model ?? "");
  const [baseUrl, setBaseUrl] = useState(settings?.baseUrl ?? "");
  const [effort, setEffort] = useState(settings?.effort ?? "default");
  const [mode, setMode] = useState<Mode>(settings?.mode ?? "default");
  const [key, setKey] = useState("");
  const [saving, setSaving] = useState(false);

  const hasKey = settings?.providers.find((p) => p.id === provider)?.hasKey ?? false;
  const anthropic = provider === "anthropic";

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [setOpen]);

  const switchProvider = (id: ProviderId) => {
    setProvider(id);
    setKey("");
    // Model names and endpoints don't carry over between providers.
    const same = id === settings?.provider;
    setModel(same ? settings.model : id === "anthropic" ? "claude-opus-5-5" : "");
    setBaseUrl(same ? (settings.baseUrl ?? "") : "");
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setSaving(true);
    const ok = await saveSettings(
      { provider, model: model.trim(), baseUrl: baseUrl.trim(), effort, mode },
      key.trim() ? { provider, key: key.trim() } : undefined,
    );
    setSaving(false);
    if (ok) setOpen(false);
  };

  return (
    <div className="overlay" onMouseDown={() => setOpen(false)}>
      <form className="dialog" role="dialog" aria-label="Настройки" onMouseDown={(e) => e.stopPropagation()} onSubmit={(e) => void submit(e)}>
        <header className="dialog__header">
          <span>Настройки</span>
          <button type="button" className="icon-btn" onClick={() => setOpen(false)} aria-label="Закрыть">
            <X size={16} />
          </button>
        </header>

        <div className="dialog__body">
          <h3 className="dialog__section">Модель</h3>

          <Field label="Провайдер">
            <select className="input" value={provider} onChange={(e) => switchProvider(e.target.value as ProviderId)}>
              <option value="anthropic">Anthropic API (и совместимые шлюзы)</option>
              <option value="openai-compatible">OpenAI-совместимый API (Ollama, LM Studio, OpenRouter…)</option>
            </select>
          </Field>

          <Field
            label={anthropic ? "Адрес API (необязательно)" : "Адрес API"}
            hint={
              anthropic
                ? "Оставьте пустым для api.anthropic.com. Укажите адрес шлюза или прокси с API в формате Anthropic."
                : "Обычно заканчивается на /v1, например http://localhost:11434/v1 для Ollama."
            }
          >
            <input
              className="input"
              value={baseUrl}
              onChange={(e) => setBaseUrl(e.target.value)}
              placeholder={anthropic ? "https://api.anthropic.com" : "http://localhost:11434/v1"}
              spellCheck={false}
            />
          </Field>

          <Field
            label={anthropic ? "API-ключ" : "API-ключ (если требуется)"}
            hint={hasKey ? "Ключ сохранён в диспетчере учётных данных Windows. Введите новый, чтобы заменить." : "Хранится в диспетчере учётных данных Windows."}
          >
            <div className="input-row">
              <input
                className="input"
                type="password"
                value={key}
                onChange={(e) => setKey(e.target.value)}
                placeholder={hasKey ? "••••••••••••" : anthropic ? "sk-ant-…" : ""}
                autoComplete="off"
                spellCheck={false}
              />
              {hasKey && (
                <button type="button" className="btn btn--secondary" onClick={() => void saveSettings({}, { provider, key: "" })}>
                  Удалить
                </button>
              )}
            </div>
          </Field>

          <Field label="Модель">
            <input className="input" value={model} onChange={(e) => setModel(e.target.value)} placeholder={anthropic ? "claude-opus-5-5" : "например, qwen3"} spellCheck={false} />
          </Field>

          <Field label="Глубина рассуждений" hint="Выше — точнее на сложных задачах, но медленнее и дороже.">
            <select className="input" value={effort} onChange={(e) => setEffort(e.target.value)}>
              {EFFORTS.map((x) => (
                <option key={x.id} value={x.id}>
                  {x.label}
                </option>
              ))}
            </select>
          </Field>

          <h3 className="dialog__section">Разрешения</h3>
          <Field label="Режим для новых сессий" hint="В открытой сессии режим меняется сочетанием Shift+Tab.">
            <select className="input" value={mode} onChange={(e) => setMode(e.target.value as Mode)}>
              {(Object.keys(MODE_LABELS) as Mode[]).map((m) => (
                <option key={m} value={m}>
                  {MODE_LABELS[m]}
                </option>
              ))}
            </select>
          </Field>
        </div>

        <footer className="dialog__footer">
          <button type="button" className="btn btn--secondary" onClick={() => setOpen(false)}>
            Отмена
          </button>
          <button type="submit" className="btn" disabled={saving}>
            Сохранить
          </button>
        </footer>
      </form>
    </div>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="field">
      <span className="field__label">{label}</span>
      {children}
      {hint && <span className="field__hint">{hint}</span>}
    </div>
  );
}
