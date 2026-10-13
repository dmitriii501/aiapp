import type { Mode } from "../bridge/protocol";

export const MODE_LABELS: Record<Mode, string> = {
  default: "Спрашивать перед изменениями",
  acceptEdits: "Правки без подтверждения",
  plan: "Только чтение",
  bypass: "Без подтверждений (опасно)",
};

/** Marker shown next to the input, as in Claude Code: nothing for the default mode. */
export const MODE_MARKERS: Record<Mode, string> = {
  default: "",
  acceptEdits: "⏵⏵",
  plan: "⏸",
  bypass: "⏵⏵⏵",
};
