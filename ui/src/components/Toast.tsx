import { useEffect } from "react";
import { CircleAlert, X } from "lucide-react";
import { useStore } from "../store";

export function Toast() {
  const toast = useStore((s) => s.toast);
  const showToast = useStore((s) => s.showToast);

  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => showToast(undefined), 8000);
    return () => clearTimeout(t);
  }, [toast, showToast]);

  if (!toast) return null;
  return (
    <div className="notification" role="alert">
      <CircleAlert size={16} className="notification__icon" />
      <span className="notification__text">{toast}</span>
      <button className="icon-btn" onClick={() => showToast(undefined)} aria-label="Закрыть">
        <X size={14} />
      </button>
    </div>
  );
}
