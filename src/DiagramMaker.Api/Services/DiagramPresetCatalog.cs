using DiagramMaker.Domain;

namespace DiagramMaker.Services;

public sealed class DiagramPresetCatalog
{
    private static readonly DiagramPreset[] Presets =
    [
        new(
            "flow-horizontal-focused",
            "flowchart",
            "메서드 직선 흐름",
            "선택 메서드의 주요 처리와 직접 호출을 왼쪽에서 오른쪽으로 표시합니다.",
            "flowchart LR\n    n_start([\"시작\"]) --> n_call[[\"직접 호출\"]] --> n_done([\"종료\"])",
            "LR", "compact", 1, 1, 1, 20, 30),
        new(
            "flow-vertical-overview",
            "flowchart",
            "조건 분기형",
            "조건, 처리, 리턴을 위에서 아래로 읽기 쉽게 표시합니다.",
            "flowchart TB\n    n_start([\"시작\"]) --> n_check{\"조건\"}\n    n_check -->|예| n_call[[\"호출\"]]\n    n_check -->|아니오| n_done([\"종료\"])",
            "TB", "balanced", 1, 1, 1, 35, 60),
        new(
            "flow-impact-detailed",
            "flowchart",
            "반복 상세형",
            "반복, 분기, 호출 및 리턴을 넓은 화면에 상세히 표시합니다.",
            "flowchart LR\n    n_start([\"시작\"]) --> n_loop{\"반복 조건\"}\n    n_loop -->|반복| n_call[[\"호출\"]]\n    n_call -. 다음 반복 .-> n_loop\n    n_loop -->|종료| n_done([\"리턴\"])",
            "LR", "detailed", 2, 2, 2, 60, 100),

        new(
            "sequence-focused",
            "sequence",
            "핵심 호출형",
            "변경 지점에서 시작하는 핵심 호출 순서만 간결하게 표시합니다.",
            "sequenceDiagram\n    participant n_Change as 변경 지점\n    participant n_Target as 호출 대상\n    n_Change->>n_Target: 호출",
            "LR", "compact", 0, 2, 1, 8, 20),
        new(
            "sequence-caller-context",
            "sequence",
            "호출자 문맥형",
            "직접 호출자부터 변경 지점을 거쳐 피호출자로 이어지는 순서를 표시합니다.",
            "sequenceDiagram\n    participant n_Caller as 호출자\n    participant n_Change as 변경 지점\n    participant n_Target as 호출 대상\n    n_Caller->>n_Change: 호출\n    n_Change->>n_Target: 호출",
            "LR", "balanced", 1, 2, 1, 12, 40),
        new(
            "sequence-detailed",
            "sequence",
            "상세 추적형",
            "관련 호출 흐름을 최대 3단계까지 확장해 실행 순서 중심으로 표시합니다.",
            "sequenceDiagram\n    participant n_A as 호출자\n    participant n_B as 변경 지점\n    participant n_C as 하위 호출\n    n_A->>n_B: 1. 호출\n    n_B->>n_C: 2. 호출",
            "LR", "detailed", 1, 3, 1, 16, 70),

        new(
            "class-changed",
            "class",
            "변경 클래스형",
            "변경 심볼이 속한 클래스만 간결하게 표시합니다.",
            "classDiagram\n    direction LR\n    class n_Changed",
            "LR", "compact", 0, 0, 0, 15, 20),
        new(
            "class-related",
            "class",
            "관련 클래스형",
            "직접 관련 클래스와 상속·호출 관계를 세로로 표시합니다.",
            "classDiagram\n    direction TB\n    class n_Caller\n    class n_Changed\n    n_Caller --> n_Changed",
            "TB", "balanced", 1, 1, 1, 30, 50),
        new(
            "class-dependency",
            "class",
            "의존 관계형",
            "관련 클래스 의존 관계를 최대 2단계까지 가로로 표시합니다.",
            "classDiagram\n    direction LR\n    class n_Caller\n    class n_Changed\n    class n_Dependency\n    n_Caller --> n_Changed\n    n_Changed --> n_Dependency",
            "LR", "detailed", 2, 2, 2, 50, 80),

        new(
            "code-method-centered",
            "code-relation",
            "메서드 중심형",
            "선택 메서드와 직접 호출 대상만 간결한 카드 형태로 표시합니다.",
            "flowchart LR\n    n_source[\"변경 메서드\"] --> n_target[\"호출 메서드\"]",
            "LR", "compact", 0, 1, 1, 20, 30),
        new(
            "code-class-grouped",
            "code-relation",
            "클래스 그룹형",
            "관련 메서드를 소유 클래스별로 묶어 세로로 표시합니다.",
            "flowchart TB\n    subgraph n_A[\"InterfaceCustom\"]\n      n_run[\"Run\"]\n    end\n    subgraph n_B[\"Opr_Xfer\"]\n      n_target[\"runOrgReturn\"]\n    end\n    n_run --> n_target",
            "TB", "balanced", 1, 1, 1, 35, 60),
        new(
            "code-indirect-focused",
            "code-relation",
            "간접 API 강조형",
            "직접 호출과 사용자 정의 간접 API 호출을 시각적으로 구분합니다.",
            "flowchart LR\n    n_source[\"InterfaceCustom.Run\"] -. 간접 API: RunFunction .-> n_target[\"Opr_Xfer.runOrgReturn\"]",
            "LR", "detailed", 1, 1, 1, 50, 80),

        new(
            "state-vertical-compact",
            "state",
            "세로 간결형",
            "근거가 확인된 핵심 상태 전이만 세로로 표시합니다.",
            "stateDiagram-v2\n    direction TB\n    [*] --> n_Ready\n    n_Ready --> n_Done",
            "TB", "compact", 0, 0, 1, 12, 20),
        new(
            "state-horizontal",
            "state",
            "가로 전개형",
            "근거가 확인된 상태 전이를 왼쪽에서 오른쪽으로 표시합니다.",
            "stateDiagram-v2\n    direction LR\n    [*] --> n_Ready\n    n_Ready --> n_Done",
            "LR", "balanced", 0, 0, 1, 20, 30),
        new(
            "state-detailed",
            "state",
            "상세 상태형",
            "확인된 중간 상태를 포함해 상태 전이 문맥을 세로로 표시합니다.",
            "stateDiagram-v2\n    direction TB\n    [*] --> n_Ready\n    n_Ready --> n_Running\n    n_Running --> n_Done",
            "TB", "detailed", 0, 0, 2, 30, 50)
    ];

    public IReadOnlyList<DiagramPreset> List(string? type = null) => string.IsNullOrWhiteSpace(type)
        ? Presets
        : Presets.Where(preset => preset.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToArray();

    public bool Contains(string type, string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        (id.Equals("balanced", StringComparison.OrdinalIgnoreCase) ||
         Presets.Any(preset => preset.Type.Equals(type, StringComparison.OrdinalIgnoreCase) &&
                               preset.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));

    public DiagramPreset Resolve(string type, string? id)
    {
        var normalizedType = type.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(id) && !id.Equals("balanced", StringComparison.OrdinalIgnoreCase))
        {
            var selected = Presets.FirstOrDefault(preset => preset.Type == normalizedType && preset.Id == id);
            if (selected is not null)
            {
                return selected;
            }
        }

        return Presets.First(preset => preset.Type == normalizedType && preset.DetailLevel == "balanced");
    }
}
