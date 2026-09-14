import { useRef } from "react";

export function CodeSourceInput({ value, invalid, onChange }: {
  value: string; invalid: boolean; onChange: (value: string) => void;
}) {
  const numbers = useRef<HTMLDivElement>(null);
  const count = value.split(/\r\n|\r|\n/).length;
  return <div className="code-source-input">
    <div ref={numbers} className="code-source-lines" aria-hidden="true">
      <pre>{Array.from({ length: count }, (_, index) => index + 1).join("\n")}</pre>
    </div>
    <textarea aria-label="코드" className="code-block-source" rows={14} spellCheck={false} wrap="off"
      value={value} aria-invalid={invalid} placeholder="클래스, 함수 또는 함수 내부 코드를 붙여 넣으세요."
      onScroll={event => { if (numbers.current) numbers.current.scrollTop = event.currentTarget.scrollTop; }}
      onChange={event => onChange(event.target.value)} />
  </div>;
}
