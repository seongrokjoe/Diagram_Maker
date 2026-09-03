# AI Git Architecture Reviewer

사내 Git 커밋의 변경 심볼과 중요한 호출 관계만 선별해 Mermaid 다이어그램으로 만드는 내부용 애플리케이션입니다. 외부 LLM fallback, CDN, telemetry, 임의 Git URL, 저장소 build·hook 실행은 지원하지 않습니다.

## 주요 기능

- Git 변경 분석을 `커밋 선택 → 변경 심볼 선택·그룹화 → 그룹별 다이어그램`의 3단계로 수행
- Target 커밋의 첫 번째 부모를 Base로 자동 선택하며 고급 옵션에서 직접 변경 가능
- Visual Studio `.sln`/`.vcxproj` 범위를 따라 C++를 Tree-sitter로 인덱싱하고, C#은 Roslyn으로 분석
- 대상이 모호한 C++ 호출은 임의 연결하지 않고 제외
- 정적 호출 근거를 우선 사용하고, 선택 시에만 내부 LLM이 그룹 제안과 한국어 요약을 생성
- 그룹마다 Flow, Sequence, Class, State 형식과 내장 샘플 프리셋 선택
- 그룹 초안과 인덱스 결과를 30일간 보존하고 같은 커밋 범위에서 재사용
- 자연어 다이어그램도 명시적 형식과 샘플 프리셋을 사용해 출력 구조를 안정화
- Mermaid를 브라우저에서 지연 로드해 미리보기, 제한된 DSL 수정, SVG/PNG 다운로드 제공

## 로컬 실행

필수 도구는 .NET 9 SDK와 Node.js 24입니다. 저장소 루트에서 다음을 실행하고 `http://localhost:5080`을 엽니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

종료:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\stop-local.ps1
```

프론트엔드 HMR만 필요하면 `npm.cmd run dev --prefix .\web`을 사용합니다.

## Git 변경 분석 사용법

1. **저장소 관리**에서 `C:\Work\Git\MyRepository` 같은 로컬 저장소 루트를 연결 테스트 후 등록합니다.
2. **Git 변경 분석**에서 저장소를 고른 뒤 최근 목록, 메시지·SHA·작성자 검색 또는 SHA 직접 입력으로 Target 커밋을 선택합니다. `이전 커밋 50개 더 보기`로 기본 브랜치의 오래된 이력을 계속 탐색할 수 있으며 Base 직접 지정에도 같은 선택기를 사용합니다.
3. 표시할 변경 심볼을 체크하고, 드롭다운으로 그룹을 이동하거나 여러 그룹을 병합합니다.
4. 그룹별 다이어그램 형식과 샘플을 선택합니다. 필요할 때만 방향과 caller/callee 깊이를 덮어씁니다.
5. 다이어그램을 생성한 뒤 그룹 탭에서 결과와 한국어 설명을 확인하고 SVG/PNG로 저장합니다.

소스 본문은 초안에 저장하지 않습니다. LLM 요약이 필요하면 고정된 Base/Target SHA에서 제한된 diff를 다시 읽어 요청한 뒤 폐기합니다.

## 사내 LLM 연결

실제 endpoint와 모델은 `%LOCALAPPDATA%\DiagramMaker\llm-policy.json`에 둡니다. `packaging\windows\config\llm-policy.example.json`을 복사해 승인된 값으로 수정하세요.

```json
{
  "Llm": {
    "Enabled": true,
    "Endpoint": "https://llm.internal/v1/chat/completions",
    "AllowedOrigin": "https://llm.internal",
    "Model": "approved-model",
    "AllowDevelopmentStub": false
  }
}
```

**사내 LLM 점검** 메뉴와 `test-llm.cmd`는 고정된 합성 데이터만 사용합니다. 자연어 생성은 낮은 temperature와 구조화 계약을 사용하고, Git 분석의 LLM 입력은 변경 후보·정적 그래프·제한된 diff로 한정합니다.

## 검증과 오프라인 패키지

전체 검증:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

Windows x64 오프라인 패키지 생성:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-offline-win-x64.ps1
```

결과는 `artifacts/release/DiagramMaker-0.1.0-offline.11-win-x64.zip`과 SHA-256 파일입니다. 회사 정책상 GitHub Release 다운로드가 막힌 환경을 위해 이 두 파일은 소스 ZIP에도 포함되도록 관리합니다.

## 현재 제한

- C++ 매크로 확장, 조건부 컴파일 결과, 함수 포인터·가상 디스패치의 런타임 대상은 완전하게 확정하지 않습니다.
- State 다이어그램은 명시적인 상태 전이 근거가 없으면 Git 정적 분석에서 생성하지 않습니다.
- Working Tree, submodule, Git LFS, 원격 clone, PlantUML과 Excalidraw는 지원하지 않습니다.
