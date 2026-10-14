import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { X } from "lucide-react";
import { useStore } from "../store";
import type { Mode, PcApp, ProviderId, SandboxShell } from "../bridge/protocol";
import { MODE_LABELS } from "./modes";

const SANDBOX_MODES: { id: SandboxShell; label: string }[] = [
  { id: "off", label: "Выключена" },
  { id: "autonomous", label: "Для режима без подтверждений и роя" },
  { id: "always", label: "Для всех команд" },
];

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
  const sandboxReadAdd = useStore((s) => s.sandboxReadAdd);
  const sandboxReadRemove = useStore((s) => s.sandboxReadRemove);
  const pcWindows = useStore((s) => s.pcWindows);
  const pcAppAdd = useStore((s) => s.pcAppAdd);
  const pcAppRemove = useStore((s) => s.pcAppRemove);
  const setOpen = useStore((s) => s.setSettingsOpen);

  const [provider, setProvider] = useState<ProviderId>(settings?.provider ?? "anthropic");
  const [model, setModel] = useState(settings?.model ?? "");
  const [baseUrl, setBaseUrl] = useState(settings?.baseUrl ?? "");
  const [effort, setEffort] = useState(settings?.effort ?? "default");
  const [mode, setMode] = useState<Mode>(settings?.mode ?? "default");
  const [key, setKey] = useState("");
  const [braveKey, setBraveKey] = useState("");
  const [jevKey, setJevKey] = useState("");
  const [sandboxShell, setSandboxShell] = useState<SandboxShell>(settings?.sandboxShell ?? "off");
  const [sandboxNetwork, setSandboxNetwork] = useState(settings?.sandboxNetwork ?? false);
  const [pcEnabled, setPcEnabled] = useState(settings?.pcEnabled ?? false);
  const [pcVision, setPcVision] = useState(settings?.pcVision ?? false);
  const [pcAutonomous, setPcAutonomous] = useState(settings?.pcAutonomous ?? false);
  const [openApps, setOpenApps] = useState<PcApp[] | null>(null);
  const [pickedApp, setPickedApp] = useState("");
  const [saving, setSaving] = useState(false);

  const hasBraveKey = settings?.braveKey ?? false;
  const hasJevKey = settings?.jevKey ?? false;
  const sandboxRead = settings?.sandboxRead ?? [];
  const pcApps = settings?.pcApps ?? [];
  const addable = (openApps ?? []).filter((a) => !pcApps.includes(a.process));
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
      { provider, model: model.trim(), baseUrl: baseUrl.trim(), effort, mode, sandboxShell, sandboxNetwork, pcEnabled, pcVision, pcAutonomous },
      key.trim() ? { provider, key: key.trim() } : undefined,
    );
    const searchOk = ok && (braveKey.trim() ? await saveSettings({}, { provider: "brave", key: braveKey.trim() }) : true);
    const jevOk = searchOk && (jevKey.trim() ? await saveSettings({}, { provider: "jev", key: jevKey.trim() }) : true);
    setSaving(false);
    if (jevOk) setOpen(false);
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

          <h3 className="dialog__section">Поиск в интернете</h3>
          <Field
            label="Ключ Brave Search API"
            hint={
              hasBraveKey
                ? "Ключ сохранён в диспетчере учётных данных Windows. Введите новый, чтобы заменить. Вместо Brave можно указать свой SearXNG: [web] searxng_url в config.toml."
                : "Нужен для инструмента WebSearch (бесплатный тариф есть на brave.com/search/api). Вместо него можно указать свой SearXNG: [web] searxng_url в config.toml."
            }
          >
            <div className="input-row">
              <input
                className="input"
                type="password"
                value={braveKey}
                onChange={(e) => setBraveKey(e.target.value)}
                placeholder={hasBraveKey ? "••••••••••••" : "BSA…"}
                autoComplete="off"
                spellCheck={false}
              />
              {hasBraveKey && (
                <button type="button" className="btn btn--secondary" onClick={() => void saveSettings({}, { provider: "brave", key: "" })}>
                  Удалить
                </button>
              )}
            </div>
          </Field>

          <h3 className="dialog__section">Управление ПК</h3>
          <Field
            label="Ключ Jev (TypeSafe)"
            hint={
              hasJevKey
                ? "Ключ сохранён в диспетчере учётных данных Windows. Введите новый, чтобы заменить. Jev быстро выбирает следующий шаг на экране; текст окна уходит на api.typesafe.ai."
                : "Нужен для быстрых решений при управлении ПК (docs.typesafe.ai). Без него шаги принимает только большая модель: медленнее, но работает. Хранится в диспетчере учётных данных Windows."
            }
          >
            <div className="input-row">
              <input
                className="input"
                type="password"
                value={jevKey}
                onChange={(e) => setJevKey(e.target.value)}
                placeholder={hasJevKey ? "••••••••••••" : "ключ TypeSafe"}
                autoComplete="off"
                spellCheck={false}
              />
              {hasJevKey && (
                <button type="button" className="btn btn--secondary" onClick={() => void saveSettings({}, { provider: "jev", key: "" })}>
                  Удалить
                </button>
              )}
            </div>
          </Field>

          <label className="check">
            <input type="checkbox" checked={pcEnabled} onChange={(e) => setPcEnabled(e.target.checked)} />
            <span>Разрешить Kairo управлять программами из списка</span>
          </label>
          <label className="check">
            <input type="checkbox" checked={pcVision} onChange={(e) => setPcVision(e.target.checked)} />
            <span>Разрешить скриншоты и клики по координатам (нужна модель, читающая изображения)</span>
          </label>
          <label className="check">
            <input type="checkbox" checked={pcAutonomous} onChange={(e) => setPcAutonomous(e.target.checked)} />
            <span>Не спрашивать в режиме «Без подтверждений» (только для программ из списка)</span>
          </label>
          <Field
            label="Программы, которыми можно управлять"
            hint="Только открытые сейчас программы, выбираются из списка. Терминалы, менеджеры паролей и системные инструменты добавить нельзя. Без флажка выше каждое действие подтверждается, даже без подтверждений; клавиша Pause останавливает управление."
          >
            <div className="path-list">
              {pcApps.map((p) => (
                <div key={p} className="path-list__row">
                  <span className="path-list__path">{p}</span>
                  <button type="button" className="btn btn--secondary" onClick={() => void pcAppRemove(p)}>
                    Убрать
                  </button>
                </div>
              ))}
              {openApps === null ? (
                <button type="button" className="btn btn--secondary" onClick={() => void pcWindows().then((a) => { setOpenApps(a); setPickedApp(""); })}>
                  Добавить из открытых окон…
                </button>
              ) : (
                <div className="input-row" style={{ width: "100%" }}>
                  <select className="input" value={pickedApp} onChange={(e) => setPickedApp(e.target.value)}>
                    <option value="">{addable.length === 0 ? "Нет подходящих открытых программ" : "Выберите программу"}</option>
                    {addable.map((a) => (
                      <option key={a.process} value={a.process}>
                        {a.process} — {a.title}
                      </option>
                    ))}
                  </select>
                  <button
                    type="button"
                    className="btn btn--secondary"
                    disabled={!pickedApp}
                    onClick={() => void pcAppAdd(pickedApp).then(() => { setOpenApps(null); setPickedApp(""); })}
                  >
                    Добавить
                  </button>
                </div>
              )}
            </div>
          </Field>

          <h3 className="dialog__section">Песочница для команд</h3>
          <Field
            label="Где запускать команды Shell в песочнице"
            hint="Команды видят только папку сессии, а сети нет. git внутри не работает. Файловые инструменты тоже остаются в этой папке."
          >
            <select className="input" value={sandboxShell} onChange={(e) => setSandboxShell(e.target.value as SandboxShell)}>
              {SANDBOX_MODES.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.label}
                </option>
              ))}
            </select>
          </Field>
          {sandboxShell !== "off" && (
            <>
              <label className="check">
                <input type="checkbox" checked={sandboxNetwork} onChange={(e) => setSandboxNetwork(e.target.checked)} />
                <span>Разрешить команде выходить в сеть</span>
              </label>
              <Field label="Папки, которые команды могут читать" hint="Например, установленный Python: он лежит в профиле, и без этого команда его не увидит. Папка выбирается в системном диалоге.">
                <div className="path-list">
                  {sandboxRead.map((p) => (
                    <div key={p} className="path-list__row">
                      <span className="path-list__path" title={p}>
                        {p}
                      </span>
                      <button type="button" className="btn btn--secondary" onClick={() => void sandboxReadRemove(p)}>
                        Убрать
                      </button>
                    </div>
                  ))}
                  <button type="button" className="btn btn--secondary" onClick={() => void sandboxReadAdd()}>
                    Добавить папку…
                  </button>
                </div>
              </Field>
            </>
          )}

          <h3 className="dialog__section">Разрешения</h3>
          <Field label="Режим для новых сессий" hint={mode === "bypass" ? "Агент выполняет всё без вопросов, включая команды Shell и запросы в сеть. Откатываются только правки файлов. Правила deny продолжают действовать. Shift+Tab переключает и на этот режим." : "В открытой сессии режим меняется сочетанием Shift+Tab."}>
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
