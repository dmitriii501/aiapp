import { useEffect } from "react";
import { X } from "lucide-react";
import { useStore } from "../store";
import { Markdown } from "./Markdown";

export function UpdateDialog() {
  const update = useStore((s) => s.update);
  const setOpen = useStore((s) => s.setUpdateOpen);
  const { checkUpdate, installUpdate } = useStore.getState();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && setOpen(false);
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [setOpen]);

  if (!update) return null;
  const busy = update.state === "checking" || update.state === "downloading";

  return (
    <div className="overlay" onMouseDown={() => setOpen(false)}>
      <div className="dialog" role="dialog" aria-label="Обновления" onMouseDown={(e) => e.stopPropagation()}>
        <header className="dialog__header">
          <span>Обновления</span>
          <button type="button" className="icon-btn" onClick={() => setOpen(false)} aria-label="Закрыть">
            <X size={16} />
          </button>
        </header>

        <div className="dialog__body">
          {update.state === "disabled" && (
            <>
              <p className="update__versions">Версия {update.currentVersion}</p>
              <p className="update__warning">
                Автообновление работает только в установленной версии Kairo с настроенным адресом обновлений. Эта сборка запущена не из установщика.
              </p>
            </>
          )}

          {update.state === "idle" && <p className="update__versions">Установлена последняя версия: {update.currentVersion}.</p>}
          {update.state === "checking" && <p className="update__versions">Проверяю обновления…</p>}

          {(update.state === "available" || update.state === "downloading") && (
            <>
              <p className="update__versions">
                Доступна версия {update.version}. Сейчас установлена {update.currentVersion}.
              </p>
              {update.notes && (
                <div className="update__notes">
                  <Markdown text={update.notes} />
                </div>
              )}
              {update.state === "downloading" ? (
                <>
                  <div className="progress" role="progressbar" aria-valuenow={update.progress ?? 0} aria-valuemin={0} aria-valuemax={100}>
                    <div className="progress__bar" style={{ width: `${update.progress ?? 0}%` }} />
                  </div>
                  <p className="update__warning">Загрузка: {update.progress ?? 0}%. После неё Kairo перезапустится.</p>
                </>
              ) : (
                <p className="update__warning">Kairo перезапустится. Открытые терминалы закроются, сессии сохранятся.</p>
              )}
            </>
          )}

          {update.state === "error" && <p className="update__error">{update.error ?? "Не удалось проверить обновления."}</p>}
          {update.state === "available" && update.error && <p className="update__error">{update.error}</p>}
        </div>

        <footer className="dialog__footer">
          {update.state !== "disabled" && update.state !== "available" && (
            <button type="button" className="btn btn--secondary" disabled={busy} onClick={() => void checkUpdate()}>
              {update.state === "error" ? "Повторить" : "Проверить сейчас"}
            </button>
          )}
          {update.state === "available" && (
            <>
              <button type="button" className="btn btn--secondary" onClick={() => setOpen(false)}>
                Позже
              </button>
              <button type="button" className="btn" onClick={() => void installUpdate()}>
                Установить и перезапустить
              </button>
            </>
          )}
        </footer>
      </div>
    </div>
  );
}
