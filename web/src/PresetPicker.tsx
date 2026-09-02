import { MermaidPreview } from "./MermaidPreview";
import type { DiagramPreset } from "./types";

export function PresetPicker({
  presets,
  selectedId,
  onSelect,
}: {
  presets: DiagramPreset[];
  selectedId: string;
  onSelect: (preset: DiagramPreset) => void;
}) {
  return (
    <div className="preset-grid" role="radiogroup" aria-label="다이어그램 샘플">
      {presets.map((preset) => (
        <button
          type="button"
          role="radio"
          aria-checked={selectedId === preset.id}
          className={`preset-card ${selectedId === preset.id ? "active" : ""}`}
          key={preset.id}
          onClick={() => onSelect(preset)}
        >
          <MermaidPreview source={preset.thumbnailDsl} compact />
          <strong>{preset.name}</strong>
          <span>{preset.description}</span>
          <small>{preset.direction} · {preset.detailLevel} · 최대 {preset.maximumNodes}개 노드</small>
        </button>
      ))}
    </div>
  );
}
