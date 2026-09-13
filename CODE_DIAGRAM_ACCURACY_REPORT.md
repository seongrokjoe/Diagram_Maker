# 코드 다이어그램 실행 정확도 개선

시험 버전은 `0.1.0-accuracy.1`이며 승인 계획은 `CODE_DIAGRAM_ACCURACY_PLAN.md`다.
기준 커밋은 `883b8cdca2d6202bba8501d97edcbf030c9edfee`다. 기존 perf.4 소스 스냅샷
239개 파일과 ZIP 해시, 기능 전 체크포인트 `e53efc2`를 확인했으며 사용자 변경을 보존한다.

## 변경 동작

- C++/C# AST에서 관측 호출, 조건 평가, 대입, 반환, 예외, 반복과 종료 대상을 원본 위치와
  함께 추출한다. 호출 관측과 대상 해석을 구분하며 미해결 호출도 표시한다.
- Sequence에 진입 함수의 전체 호출과 첫 실패별 조기 반환, 정상 반환을 함께 표시한다.
  같은 이름의 호출도 위치가 다르면 남긴다. 내부 함수는 상세 탐색, 독립 진입점은 별도
  시나리오로 구성한다. C++ 인자 사이의 평가 순서와 외부 반환값은 불확실성을 표시한다.
- 단락 평가와 반복 조건·본문·증감을 분리한다. break/continue 이후 도달할 수 없는 호출을
  추가하지 않으며 종료 대상을 보존한다. 지역 상수·복사·분기 병합으로 반환값을 추적하고
  별칭·필드·외부 변경에 영향을 받는 값은 보수적으로 미확인 처리한다.
- 함수 원문과 실행 사실로 전체 의미 계획을 만들고, 구조 검증 뒤 함수 전체 의미를
  검토한다. 호출 누락·중복·재정렬, 조건 반전, 반환·종료 변경과 제어 경계를 넘는 묶음을
  거부한다. 함수 계획은 출력 형식 간 공유한다.
- 긴 함수는 완결된 형제 영역 사이에서 나누며 각 요청의 입력·출력 예산을 검사한다.
  재결합한 전체 원문과 사실을 검토한다. 단일 제어 영역이나 최종 검토가 예산을 넘으면
  원문을 자르지 않고 미완료로 표시한다. 수정 최대 1회와 기존 실행 예산을 유지한다.
- Git에 State를 포함한 5개 형식을 연결한다. 같은 변수·출발·도착 전이의 조건만 바뀌면
  수정으로 표시하고 두 리비전의 조건·대입 근거를 제공한다. enum이나 반환 플래그만으로
  상태 전이를 만들지 않으며 외부 호출 뒤 무효화된 상태 관측은 사용하지 않는다.
- **호출·조건·반환과 원본 근거**에서 설명과 정확한 원식을 구분하고 해당 코드 위치를
  연다. 구버전 중단 실행은 불변 입력에서 새 분석으로 시작하며 과거 결과·근거·편집을
  보존한다. 분석 캐시는 `code-block-v4` / `source-graph-v11`, 공유 의미는
  `shared-semantic-v2`로 구분한다.

## 중단 이후 해결한 문제

Git 5형식 API 회귀에서 `if` 분기와 조건 안의 단락·삼항식 평가가 같은 사실 ID를 사용해
전체 의미 계획의 범위 검증과 Sequence 컴파일이 실패했다. C++와 C#의 if 식별자를 별도로
만들어 조건 원문·위치는 유지하면서 ID 충돌을 제거했다. `&&`, `||`, 삼항식과 Unicode/CRLF
위치 회귀를 추가했다. 관련 .NET 21개와 C++ 파서 13개, Git 5형식·State 변경 API가 통과했다.

모바일 근거 탐색 후 결과 패널의 최소 너비가 창 너비를 초과하는 현상을 재현했다.
결과 격자 열과 편집기·패널의 최소 폭, 줄바꿈, 세로 근거 목록의 폭을 보정하고 기존 390px
검증에 넘치는 요소의 크기 진단을 추가했다. `code-block-ui-RkVgR5`에서 1440/390px,
25개 실행 근거 탐색, SVG 다운로드·복원을 포함한 UI 회귀가 통과했고 모바일 캡처를 직접
확인했다. Browser 연결이 없어 기존 격리 Headless Edge를 사용했다.

## 검증과 전달 상태

1,212줄·40함수 합성 C++ 사례에서 기본 및 60,000자 제한 모두 아래 결과가 통과했다.
함수 전체 계획·검토와 실행 근거가 추가되므로 이전 perf.4보다 요청 수가 증가한다.
결과 재사용 시 추가 요청은 없다.

| 사례 | 함수 계획·검토 | 표현 생성 | 표현 검토 | 전체 요청 | 페이지 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 코드 Flow | 80 | 7 | 7 | 94 | 41 |
| 코드 Flow·Class·관계도 | 80 | 11 | 11 | 102 | 43 |
| Git 3형식 | 160 | 48 | 48 | 256 | 51 |

별도 Git 5형식 API에서 State 조건 변경의 수정 표시·양쪽 근거와 Sequence의 호출 2개,
반환 false/true를 확인했다. 코드 생성 자체 검사는 정상 12회, 스키마 호환 복구 16회,
일반 HTTP 오류 1회 뒤 중단을 확인했다. 응답 원문·비밀 주소가 보고서에 노출되지 않는다.

최종 전체 `verify.ps1`은 exit 0이다. .NET 279개, 작업자 37개, 프런트엔드 46개,
정책 7개·시작 정책 11개, 빌드·라이선스·API 두 모드·공유 의미 두 문자 한도·SVG 6개·
Edge UI가 통과했다. 로그는 `artifacts/accuracy-validation/accuracy-verify-release.log`,
공유 의미 증적은 `shared-semantics-1mQvp0`/`shared-semantics-Yg38VV`, UI는
`code-block-ui-71E039`에 보존한다.

Windows 빌드의 첫 시도는 검증 작업본의 Node ZIP 캐시 누락으로 중단됐다. 저장소에
보존된 사전 반입 ZIP을 승인 체크섬과 검증하고 복원했으며 외부 다운로드는 하지 않았다.
재실행한 `build-offline-win-x64.ps1 -Version '0.1.0-accuracy.1' -SkipTests`는 exit 0이다.
내장 x64 Node로 작업자 37개·프런트엔드 46개를 검사하고 self-contained 게시,
라이선스/SBOM 검증과 ZIP 생성을 완료했다. ZIP 크기는 94,291,568바이트다.
ZIP 감사와 저장소 전달 복사가 통과했다.

- 파일: `artifacts/release/DiagramMaker-0.1.0-accuracy.1-win-x64.zip` 및 같은 이름의 `.sha256`.
- SHA-256: `cd293e7d1b2190ca9245e10682c307d2bdbe33c6ab28d09325e34443a3fdc119`.
- ZIP의 1,821개 파일이 배포 폴더와 일치하며 소스 239개가 검증 작업본과 일치한다.
- 패키지의 실행 사실/C++/코드 블럭 작업자, Windows 안내와 설정 예제가 원본과 일치한다.
- 실제 정책·런타임 data·저장소 이력이 ZIP에 없으며 기존 perf.4 ZIP/SHA를 보존했다.
- `.git` 없는 비동기화 작업본에서 빌드해 manifest의 sourceCommit은 `unavailable`,
  sourceTreeDirty는 null이다. 정확한 소스는 `artifacts/accuracy-validation/source-inventory.json`으로 식별한다.

실제 배포 EXE를 사용한 7개 검증 명령이 모두 exit 0이다.

| 검사 | 결과/증적 폴더 |
| --- | --- |
| 미리보기·API | 5종 코드 API, 기존 4종 × 4개 화면 폭 · `offline-preview-i5hq0a` |
| UI·근거·편집·복원·다운로드 | 실행 근거 25개, 1440/390px 포함 · `code-block-ui-xy9hDA` |
| 공유 의미 기본 설정 | 위 요청 수/페이지 수, Git 5형식·State 및 자체 검사 · `shared-semantics-YazGrR` |
| 공유 의미 60,000자 | 동일 요청 수/페이지 수와 자체 검사 · `shared-semantics-30IMRZ` |
| LLM 제한 모드 | 합성 전송 5개 · `packaged-llm-7WsGwk` |
| LLM 기본 모드 | 합성 전송 5개 · `packaged-llm-gnoGGo` |
| Windows 실행기 | 실패 보고서 보존·종료 코드 등 7개 · `windows-launchers-R7KbBZ` |

배포 실행 근거와 모바일 자체 검사 캡처를 직접 확인했다. 선별 증적 204개는
`artifacts/accuracy-validation/`에 보존한다. `package-audit.json`은 ZIP 감사,
`source-inventory.json`은 검증 소스 해시, `evidence-inventory.json`은 증적 해시,
`accuracy-package-results.json`은 7개 검증 명령의 종료 결과다.
PowerShell 로그는 내용을 보존하고 UTF-8로 정규화했다.
`final-delivery.json`에 소스·증적·ZIP/SHA·7개 종료 결과의 최종 대조와
`git diff --check` 통과를 기록했다.

로컬 구현·검증·Windows 패키지 전달을 완료했으며 미해결 로컬 검사 실패는 없다.
변경은 작업 트리에 보존했고 이번 작업에서 커밋/원격 푸시는 수행하지 않았다.

사내 실제 LLM의 의미 품질·완료 시간과 PostgreSQL 실연결은 승인 계획대로 이 PC에서
시험하지 않는다. 합성 loopback 응답은 계약·구조·실패 복구 검증이며 실제 모델 품질과 구분한다.

## 재현 명령

비동기화 검증 작업본 `%LOCALAPPDATA%\Temp\DiagramMaker-performance-20260910`에서 실행한다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\build-offline-win-x64.ps1 -Version '0.1.0-accuracy.1' -SkipTests
$package = 'artifacts/stage/DiagramMaker-0.1.0-accuracy.1-win-x64'
node scripts/smoke-offline-preview.mjs $package
node scripts/smoke-code-block-ui.mjs $package
node scripts/smoke-shared-semantics.mjs $package
node scripts/smoke-shared-semantics.mjs $package --characters-60000
node scripts/smoke-packaged-llm.mjs $package
node scripts/smoke-packaged-llm.mjs $package --basic
node scripts/smoke-windows-launchers.mjs $package
```

`-SkipTests`는 직전 전체 verify를 통과한 동일 소스의 .NET 검사만 생략한다.
패키징의 내장 x64 Node 작업자·프런트엔드 검사와 게시·라이선스 검사는 계속 수행한다.
같은 버전 ZIP이 이미 존재하면 새 버전을 지정한다.
