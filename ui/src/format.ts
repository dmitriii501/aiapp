const rtf = new Intl.RelativeTimeFormat("ru", { numeric: "auto" });
const dateFmt = new Intl.DateTimeFormat("ru", { day: "numeric", month: "short" });

/** "только что", "5 минут назад", "вчера", "12 окт." */
export function relativeTime(iso: string, now = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 45) return "только что";
  if (abs < 3600) return rtf.format(Math.round(seconds / 60), "minute");
  if (abs < 86400) return rtf.format(Math.round(seconds / 3600), "hour");
  if (abs < 2 * 86400) return rtf.format(Math.round(seconds / 86400), "day");
  return dateFmt.format(new Date(iso));
}

export function folderName(path: string): string {
  const parts = path.replace(/[\\/]+$/, "").split(/[\\/]/);
  return parts[parts.length - 1] || path;
}
