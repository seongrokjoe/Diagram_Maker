# AI Git Architecture Reviewer

사내 Git 커밋의 변경 심볼과 중요한 호출 관계만 선별해 Mermaid 다이어그램으로 만드는 내부용 애플리케이션입니다. 외부 LLM fallback, CDN, telemetry, 임의 Git URL, 저장소 build·hook 실행은 지원하지 않습니다.

## 주요 기능

- Git 변경 분석을 `커밋 선택 → 변경 심볼 선택·그룹화 → 그룹별 다이어그램`의 3단계로 수행
- Target 커밋의 첫 번째 부모를 Base로 자동 선택하며 고급 옵션에서 직접 변경 가능
- Visual Studio `.sln`/`.vcxproj` 범위를 따라 C++를 Tree-sitter로 인덱싱하고, C#은 Roslyn으로 분석
- 대상이 모호한 C++ 호출은 임의 연결하지 않고 제외하며 파일별 접이식 진단으로 표시
- 정적 호출 근거를 우선 사용하고, 선택 시에만 내부 LLM이 그룹 제안과 한국어 요약을 생성
- 그룹마다 메서드 흐름도, Sequence, Class, 코드 관계도, State 중 최대 4개 형식과 내장 샘플 프리셋 선택
- C++ 흐름도는 조건·반복·호출·리턴을 구문 근거로 표시하고 Sequence는 loop/alt/return을 표현
- 그룹 초안과 인덱스 결과를 30일간 보존하고 같은 커밋 범위에서 재사용
- 완료된 분석의 단계 이동과 최근 생성 이력 선택을 지원하며, 실제 Git 변경 줄과 겹치는 심볼·제어 노드·호출 관계를 빨간색 배지와 범례로 표시
- 자연어 다이어그램도 요청 하나에 최대 4개 형식과 샘플 프리셋을 적용하고 결과를 하나의 이력으로 관리
- Mermaid를 브라우저에서 지연 로드해 미리보기, 렌더링된 노드·관계 직접 삭제, 실행 취소·다시 실행, 구조 편집 리비전, SVG/PNG 다운로드 제공

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
   - `RunFunction(targetClass, targetMethod)`처럼 문자열 기반 사내 API를 사용하면 해당 저장소의 **간접 호출 규칙**에 API명과 인자 번호를 등록합니다.
   - 문자열 값이 런타임에 결정되면 `m_strFunctionOprXfer → Opr_Xfer` 형태의 변수 별칭을 추가합니다.
2. **Git 변경 분석**에서 저장소를 고른 뒤 최근 목록, 메시지·SHA·작성자 검색 또는 SHA 직접 입력으로 Target 커밋을 선택합니다. `이전 커밋 50개 더 보기`로 기본 브랜치의 오래된 이력을 계속 탐색할 수 있으며 Base 직접 지정에도 같은 선택기를 사용합니다.
3. 표시할 변경 심볼을 체크하고, 드롭다운으로 그룹을 이동하거나 여러 그룹을 병합합니다.
4. 그룹별로 필요한 다이어그램 형식을 추가하고 각각의 샘플과 고급 옵션을 선택합니다. `최종 적용` 요약에서 실제 방향·상세도·깊이를 확인할 수 있습니다. 근거가 적은 변경은 프리셋 간 결과가 같을 수 있으며, 없는 관계를 임의로 추가하지 않습니다.
5. 다이어그램을 생성한 뒤 `그룹 → 다이어그램 형식` 탭에서 결과와 한국어 설명을 확인합니다. 옵션을 바꿔 다시 생성하면 변경된 보기만 새로 만들고 나머지는 재사용합니다.
6. 결과의 **구조 편집**에서 렌더링된 노드·관계를 직접 선택해 삭제하거나 목록에서 추가·이름 변경·정렬·방향 변경을 수행할 수 있습니다. 수정은 원본을 덮어쓰지 않고 새 리비전으로 저장되며 SVG/PNG로 내려받을 수 있습니다.

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

결과는 `artifacts/release/DiagramMaker-0.1.0-offline.15-win-x64.zip`과 SHA-256 파일입니다. 회사 정책상 GitHub Release 다운로드가 막힌 환경을 위해 이 두 파일은 소스 ZIP에도 포함되도록 관리합니다.

## 현재 제한

- C++ 조건부 컴파일 결과, 함수 포인터·가상 디스패치의 런타임 대상은 완전하게 확정하지 않습니다. 간접 호출 규칙은 문자열 리터럴, 단순 매크로·상수·대입 및 명시적 별칭만 해석합니다.
- State 다이어그램은 명시적인 상태 전이 근거가 없으면 Git 정적 분석에서 생성하지 않습니다.
- Working Tree, submodule, Git LFS, 원격 clone, PlantUML과 Excalidraw는 지원하지 않습니다.
