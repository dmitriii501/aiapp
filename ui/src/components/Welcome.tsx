import { AlertTriangle, FolderOpen, Settings } from "lucide-react";
import { useStore } from "../store";

/** Start page, in the spirit of an editor's welcome tab. */
export function Welcome() {
  const settings = useStore((s) => s.settings);
  const workspaces = useStore((s) => s.workspaces);
  const startSession = useStore((s) => s.startSession);
  const pickFolder = useStore((s) => s.pickFolder);
  const setSettingsOpen = useStore((s) => s.setSettingsOpen);
  const notReady = settings && !settings.ready;

  return (
    <div className="welcome">
      <h1 className="welcome__title">Kairo</h1>
      <p className="welcome__subtitle">Агент для работы с кодом и файлами на вашем компьютере</p>

      {notReady && (
        <div className="banner">
          <AlertTriangle size={14} />
          <span>Модель не подключена. Укажите API-ключ или адрес локального сервера.</span>
          <button className="link" onClick={() => setSettingsOpen(true)}>
            Открыть настройки
          </button>
        </div>
      )}

      <div className="welcome__columns">
        <section>
          <h2 className="welcome__heading">Начать</h2>
          <button className="link link--row" onClick={() => void pickFolder()}>
            <FolderOpen size={15} />
            Открыть папку…
          </button>
          <button className="link link--row" onClick={() => setSettingsOpen(true)}>
            <Settings size={15} />
            Настройки модели
          </button>
        </section>

        <section>
          <h2 className="welcome__heading">Недавние</h2>
          {workspaces.length === 0 && <p className="welcome__muted">Здесь появятся папки, с которыми вы работали.</p>}
          {workspaces.map((w) => (
            <div key={w.path} className="recent">
              <button className="link" onClick={() => void startSession(w.path)}>
                {w.name}
              </button>
              <span className="recent__path">{w.path}</span>
            </div>
          ))}
        </section>
      </div>
    </div>
  );
}
