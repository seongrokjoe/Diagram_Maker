import { useEffect, useRef, useState } from "react";

export function useElapsedSeconds(active: boolean) {
  const startedAt = useRef(0);
  const [seconds, setSeconds] = useState(0);
  useEffect(() => {
    if (!active) { startedAt.current = 0; setSeconds(0); return; }
    startedAt.current = Date.now();
    setSeconds(1);
    const timer = window.setInterval(() => setSeconds(Math.floor((Date.now() - startedAt.current) / 1000) + 1), 250);
    return () => window.clearInterval(timer);
  }, [active]);
  return seconds;
}

export function elapsedLabel(label: string, active: boolean, seconds: number) {
  return active ? `${label} (${seconds}초)` : label;
}
