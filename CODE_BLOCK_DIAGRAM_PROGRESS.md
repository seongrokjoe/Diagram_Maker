# 코드 블럭 다이어그램 구현 진행 기록

다음 세션은 이 파일과 `CODE_BLOCK_DIAGRAM_IMPLEMENTATION_PLAN.txt`, `AGENTS.md`를 먼저 읽는다.
사용자는 전체 계획 구현을 승인했으며 구현 전에 기존 작업 전체를 커밋/푸시하도록 지시했다.
허가된 작업을 이어서 수행하고, 기존 변경을 삭제하거나 덮어쓰지 않는다.

## 현재 상태

- 2026-09-11 perf.1 배포 푸시/지난 ZIP 정리 승인: 사용자 요청으로 배포 폴더의
  internal.5 ZIP(94,167,458바이트)과 체크섬을 정리하고 perf.1 ZIP/SHA만 남겼다.
  삭제 목록·원래 해시·복구 커밋은 `artifacts/performance-validation/removed-packages.json`.
  기존 Git 이력과 체크포인트는 보존하며 소스 212개/새 ZIP은 검증 당시와 동일하다.
  원격 origin/main은 f6be5d3로 로컬 기준과 같음. 다음: 소스·시험 패키지·선별 검증 증적 커밋 →
  origin/main 일반 push → 원격 SHA 및 배포 파일 확인. 실제 사내 LLM 5분/품질은 계속 미검증이다.

- 2026-09-11 성능 개선 로컬 구현·검증·시험 패키지 완료. 전체 verify와 실제 패키지 검사
  6개(미리보기, 코드 블럭 UI, 공유 의미 API, LLM 전송 두 모드, Windows 실행기) 모두 exit 0.
  배포 EXE에서도 고정 C++ Flow 4회/41페이지, 3형식 6회/43페이지, Git 20회/51페이지 통과.
  `artifacts/release/DiagramMaker-0.1.0-perf.1-win-x64.zip` 및 SHA 파일 생성·복사 완료:
  94,208,344바이트/1,818파일, SHA-256 `2c9b0049d3b442efe41a29ba27f5cbecb8c4e0a8498e0243927590faac9ad99e`.
  ZIP 모든 파일과 배포 폴더 일치, 소스 212개와 검증 작업본 일치, internal.5 원래 해시 보존 확인.
  로그·화면·진단·소스 목록은 `artifacts/performance-validation/`, 결과 설명은 `CODE_DIAGRAM_PERFORMANCE_REPORT.md`.
  소스는 기준 f6be5d3 이후 미커밋 상태이며 이번 작업에서 커밋/푸시는 하지 않았다.
  다음 구체적 행동: 사내 고정 코드/실제 문제 코드 각각 3회, Thinking OFF, 전체 300초/의미 품질 확인.
  실제 사내 LLM 및 PostgreSQL은 이 PC에서 미실시이며 미해결 로컬 실패는 없다.

- 2026-09-11 전체 verify 통과(exit 0): .NET 195, 작업자 32, 웹 46, 정책 7,
  시작 정책 11, API 두 모드, 공유 의미 API(4/6/20회), SVG 6종 및 Edge UI 통과.
  UI에서 생성 중 페이지 요청 0회, 전체/이번 실행 집계, 5분 경과 안내, 중단 후 부분 열기와
  1440/390px 진행 표시를 검증했다. 증적은 작업본 `artifacts/performance-verify-final.log`,
  `shared-semantics-s29Yuq`, `code-block-ui-VddtGu`. 다음은 perf.1 빌드 및 배포 실행 파일 검증.

- 2026-09-11 전체 검증 보완: Thinking의 큰 출력 예약이 48,000 입력 묶음 판정에서
  중복 적용되어 기존 회귀가 실패한 점을 수정했고 .NET 195/195 통과를 확인했다.
  작업자 32/32, 프런트엔드 46/46, 정책 7/7 통과. API 검사에 남은 진단 v1 기대값은 v2로 갱신했다.
  현재 전체 verify의 실제 API/Edge 화면 검사를 계속하며 다음은 perf.1 패키지 빌드·실행 검증이다.
  internal.5 SHA-256은 `c71bfe6b35cdd71ce047766426039f5b2cd5e5482408fac74ceba310616ef2ed`로 변경 없다.

- 2026-09-11 성능/API 단계 통과: 공유 의미 11/11(코드/Git 두 차례 LocalFile 재개, 페이지 구성 중
  두 차례 중단·결과 보존, 분할·영구 실패 한도, 다섯 형식 근거 보존) 통과.
  실제 API 합성 검사 `shared-semantics-PlUfhx` 통과: 1,212줄/40함수 C++ Flow 4회/41페이지,
  3형식 6회/43페이지, 동일 Git 변경 3형식 20회/51페이지. 동일 코드 결과 재사용 시 추가 요청 0회.
  Git의 중복 근거 메타데이터와 무작위 순서 묶음을 제거하여 같은 원본 범위를 함께 처리한다.
  메시지의 실제 문자열과 JSON 전송 이스케이프를 분리하여 토큰 추정의 중복 계산도 수정했다.
  다음: 전체 `scripts/verify.ps1` → 화면 상태/진단 v2 확인 → `0.1.0-perf.1` 시험 ZIP/패키지 검사.
  실모델의 품질 및 5분 달성은 아직 미검증이며 internal.5 복구본은 보존한다.

- 2026-09-11 성능 개선 재개: 공유 의미 회귀 9/9 통과(새 버전 LocalFile 두 차례 재시작/재개,
  완료 HTTP 요청 재전송 금지, 분할/검토 거절/계약 오류의 유한 처리, 5종 제어·호출·상태·사용자 근거).
  전체/이번 실행·전송·대기 UI, 진단 v2, 전체 완료 후 표시 및 중단 부분 결과 열기를 연결했고 프런트엔드 빌드 통과.
  단위 성능 사례는 C# 12회/C++ 8회. 실제 API 합성 검사에서는 토큰 추정이 JSON 이스케이프를
  중복 계산하여 C++ 다중 형식이 14회가 되는 원인을 발견했고 메시지 본문 바이트 기준으로 수정했다.
  직전 재검증은 자동 승인 검토의 사용량 한도 오류로 중단되었으며 사용자 재개 요청 후 다시 실행 중이다.
  다음: `node scripts/smoke-shared-semantics.mjs` 결과 확인 → Git 재개·UI 회귀 → 전체 verify → 새 시험 ZIP/패키지 검사.
  검증 작업본: `%LOCALAPPDATA%/Temp/DiagramMaker-performance-20260910`. internal.5 및 기존 변경 보존.
  실제 사내 LLM 300초/품질 시험은 사용자 확정대로 미실시이며 합성 응답 통과와 구분한다.

- 2026-09-10 성능 개선 재개: 기존 변경과 internal.5를 보존하고 비동기화 작업본에서 현재 회귀를 실행했다.
  실행 회귀 12/12 통과, 공유 의미 회귀는 1/4 통과. C# 고정 사례 요청 20회(목표 12회 초과),
  C++ 테스트 환경 미설정, Git 요약 페이지 변경 근거 검증 실패를 확인했다.
  다음: 중복 문맥/묶음 예산과 요약 검증 보완 → 새 버전 재시작·두 차례 재개/실패 회귀 → 화면/진단 v2 → 전체 verify/시험 ZIP.
  실제 사내 LLM 5분 목표는 사용자 확정대로 이 PC에서 검증하지 않는다.

- 2026-09-10 성능 개선 P1/P2 진행: 공통 완료/누적/이번 실행 집계, 실제 HTTP 재시도 계측,
  실패·검토 거절 저장과 Git 부분 결과 보존을 구현했다. 새 작업 버전과 코드 블럭/Git의 공유 의미 묶음 생성 경로를 연결 중이다.
  비동기화 검증 경로는 `%LOCALAPPDATA%/Temp/DiagramMaker-performance-20260910`.
  최초 기존 실행 회귀 8/8 통과. 신규 회귀 포함 빌드 통과 후 11/12 통과했고, 남은 1개는 구버전
  페이지 공개 시점에 의존하는 예산 테스트여서 명시적으로 구버전 실행을 검증하도록 보완했다. 재실행 필요.
  다음: 공유 응답 합성 fixture/요청 수·의미 보존·신버전 두 차례 재개 회귀 → 화면/진단 v2 → 전체 verify/새 패키지.
  아직 구현/검증 완료가 아니며 12회/5분 목표는 미검증이다.

- 2026-09-10 성능 개선 실행 시작: 승인 계획은 `CODE_DIAGRAM_PERFORMANCE_PLAN.md`.
  기준 `f6be5d3`의 깨끗한 작업 트리와 internal.5 ZIP을 보존한다. 사용자 추가 지시에 따라 Git도 범위에 포함한다.
  Git의 페이지별 중복 생성/검토, 공통 카운터 초기화 및 재개 시 부분 결과 초기화 경로를 확인했다.
  다음: 공통 실행 기록/전송 계측/이어하기 회귀 → 코드 블럭·Git 공유 의미 묶음 처리 → 화면·전체 검증·새 시험 ZIP.
  현재 새 구현은 시작 단계이며 실제 사내 LLM 5분 목표는 미검증이다.

- 2026-09-10 internal.5 전달 완료: 배포 커밋 `bdfe294ae29434f1785f6e4bae7ea04d4a430c5d`를
  기존 origin/main에 일반 push했다(exit 0, `17106dd..bdfe294`). `git ls-remote`로 원격 SHA가
  배포 커밋과 같음을 확인했고 해당 커밋의 release 폴더에는 internal.5 ZIP/SHA 두 파일만 있다.
  소스·배포본·검증 증적·문서와 승인된 구버전 파일 정리가 모두 반영됐다. GitHub의 89.81 MB
  파일 크기 권고 외 실패는 없다. 기존 checkpoint와 Git 이력은 보존했다.
  다음 제품 검증은 사내 환경의 실제 LLM 의미 품질 시험과 필요한 PostgreSQL 실연결 검증이다.
  이 PC에서 가능한 구현/전체 검증/패키징/배포 작업은 완료했으며 미해결 로컬 실패는 없다.

- 2026-09-10 중단 작업 재개: 배포 커밋/푸시 직전의 변경을 확인하고 기존 승인에 따라 마무리한다.
  현재 소스는 검증한 `7bb42f6` 그대로이며 추가 제품 코드 변경은 없다. 배포 ZIP의 SHA-256,
  1,818개 파일/94,167,458바이트, manifest sourceCommit 및 7개 검증 결과의 passed 상태를 재확인했다.
  검증 로그 7개의 미반영 차이는 UTF-16 → UTF-8 변환뿐임을 확인했다. 내용은 보존하고
  diff 검사에서 발견한 줄 끝 공백 4곳만 정리했다. 기존 전체 verify/패키지 검증 결과를 유지한다.
  다음: 배포 변경 커밋 → 기존 origin/main 일반 push → 원격 SHA와 배포 파일 목록 확인.
  실제 사내 LLM 및 PostgreSQL 검증은 기존 기록대로 환경 의존 미실시 항목이다.

- 2026-09-10 internal.5 빌드·실제 패키지 검증 완료, 이전 ZIP/SHA 삭제 완료.
  소스 `7bb42f6`; API `offline-preview-OAusA8`, UI `code-block-ui-b9fn8d`, 합성 LLM 전송
  `packaged-llm-VzpcEU`/`packaged-llm-3jlzXy`, CMD 6종 `windows-launchers-Qw0HUY` 모두 통과.
  ZIP은 1,818개 파일/94,167,458바이트, SHA-256
  `c71bfe6b35cdd71ce047766426039f5b2cd5e5482408fac74ceba310616ef2ed`.
  manifest sourceCommit 일치, 전체 ZIP/stage 바이트 및 작업자/실행기/사내 시험 안내 일치를 확인했다.
  manifest의 sourceTreeDirty=true 표시는 그대로 보존했고 별도 Git 검사로 추적 소스 변경 없음과
  미추적 파일이 생성 ZIP/SHA뿐임을 기록했다. 감사의 샌드박스 Git 소유권 오류는 빌드 사용자로 읽어 해소했다.
  사용자 승인에 따라 release의 internal.4/semantic.1 ZIP/SHA 4개를 삭제하고 internal.5 ZIP/SHA만 남겼다.
  `artifacts/internal5-validation/`과 `SEMANTIC_RELIABILITY_REPORT.md`에 증적을 보존했다.
  다음: 배포 변경 커밋 → 기존 origin/main 일반 push → 원격 SHA와 배포 파일 목록 확인.

- 2026-09-10 internal.5 전체 verify 재검증 exit 0: 소스 `7bb42f6`, .NET 180/worker 32/web 46,
  정책 7/시작 11종, 기본·선택 API, SVG 6종, Edge UI `code-block-ui-GXmAGP` 통과.
  clean checkout의 시작 정책 검사 실패는 검증 웹 빌드를 명시적으로 준비하여 해결했다.
  동일 커밋의 Windows x64 패키지 빌드 중. 다음: 패키지 API/UI/합성 LLM 두 모드/CMD 실행기/ZIP 검사,
  승인된 이전 ZIP/SHA 삭제와 internal.5 배포 커밋·origin/main 푸시.

- 2026-09-10 internal.5 소스 커밋 `ccac677` 생성 후 clean worktree 전체 검증에서 시작 정책 스모크가 실패했다.
  .NET 180/worker 32/web 46과 빌드·정책·라이선스는 통과했지만 기존 로컬 web/dist가 없는 상태에서
  시작 검사가 정적 웹 디렉터리를 준비하지 않았다. 검증에서 생성한 웹 파일을 build/wwwroot에 복사하도록
  테스트 스크립트를 보완한다. 제품 코드 변경은 없다. 다음: 보완 커밋의 전체 검증 → internal.5 빌드.

- 2026-09-10 사내 시험 배포 승인: 사용자가 시험 ZIP 생성, 이전 버전 ZIP 삭제, 커밋·푸시까지 명시적으로 지시했다.
  이번 지시는 이전 ZIP 보존 조건보다 우선한다. 사내 LLM 실제 검증은 이 PC에서 계속 제외한다.
  배포 버전을 `0.1.0-internal.5`로 정하고 설치/합성 코드/의미 품질·이어하기/진단 시험 안내를 포함한다.
  다음: 소스 커밋 → 동일 커밋의 비동기화 clean 작업본에서 패키지 빌드·검증 → 구버전 ZIP/SHA 정리 → origin/main 푸시.

- 2026-09-10 의미 분석 신뢰성 개선의 로컬 구현·검증·시험 패키지 완료.
  `0.1.0-semantic.1` 빌드 exit 0. 실제 패키지 API/C/C++/5종/프리셋 검사 `offline-preview-dANhwV`,
  Edge UI/편집/내보내기/진단 다운로드 `code-block-ui-ad8BZz`, loopback LLM 전송 기본·선택 정책
  `packaged-llm-xLFYqo`/`packaged-llm-QSVnMb` 모두 통과했다. 추가 API 취소/재개·중복 거부도 통과.
  ZIP 1,817개 파일/94,163,939바이트, SHA-256
  `9f4df338eeaccdda0396d030c1fc119ddba4bc28fc25b8fe2a38b075d14f3966`.
  시험 ZIP/SHA는 `artifacts/release/`, 증적은 `artifacts/semantic-validation/`에 보존했다.
  소스 202개가 검증 작업본과 일치하고 ZIP 전체가 stage와 일치한다. 빌드 복사본의 `.git` 부재로
  manifest sourceCommit은 unavailable이며, 소스 해시 목록으로 미커밋 시험 작업본임을 기록했다.
  기준 `17106dd`와 internal.4 ZIP의 해시를 보존했다. 신규 커밋/푸시/기존 배포 교체는 하지 않았다.
  상세 결과: `SEMANTIC_RELIABILITY_REPORT.md`. 미해결 로컬 실패 없음.
  다음: 사내 환경에서 실제 LLM 품질·토큰·시간 검증, 필요한 경우 PostgreSQL 실연결·동시성 검증.
  사용자 확정대로 사내 LLM 검증은 이 PC에서 수행하지 않으며 해당 항목을 미검증으로 유지한다.

- 2026-09-10 의미 분석 개선 최종 전체 검증 exit 0: .NET 180/180, worker 32/32, web 46/46,
  정책 회귀 7종, 시작 정책 11종, 기본/선택 정책 API, SVG 보안·색상 6종 및 Edge UI 통과.
  로그: 비동기화 임시 작업본의 `artifacts/semantic-verify-final.log`, UI: `code-block-ui-gIbT6W`.
  큰 함수의 분할/근거·한글/CRLF/이모지, 허용 심벌의 1회 추가 문맥, 출력 방식 협상/토큰 예산과
  중단/재시작 후 완료 요청 재사용을 검증했다. 추가 문맥 회귀의 잘못된 테스트 분석기 선택을 수정했고,
  SVG 허용 태그에 정상 sequence 정의인 symbol을 보존하여 모든 5종 렌더 검사를 통과했다.
  다음: 추가 취소·재개 HTTP/진단 다운로드 회귀 → `0.1.0-semantic.1` 시험 ZIP과 실제 패키지 검사.
  사내 실제 LLM 검증은 사용자 지시에 따라 이 PC에서 수행하지 않는다.

- 2026-09-10 재개/안전성 회귀: .NET 173/173, web 46/46 및 프런트엔드 빌드 통과.
  실제 VllmClient 전송 계층을 합성 HTTP 응답에 연결하여 요약 저장 → 예산 중단 → LocalFile 재시작 →
  완료 요청 재호출 없이 나머지 생성, 소유자/중복 재개 거부를 검증했다. Git 작업에도 lease/CAS/갱신,
  체크포인트·진단·부분 페이지 저장과 취소/재개를 연결했다. SVG 5종의 계산된 색/선·PNG 변환과
  악성 CSS 제거 통과. CSS hex가 rgb로 정규화되는 테스트 비교 오류는 계산된 색상 비교로 수정했다.
  다음: 큰 입력/전송 협상·분할 회귀 보완 → 최종 전체 verify(Edge 포함) → 새 시험용 ZIP/패키지 검사.

- 2026-09-10 중단 작업 재개: `SEMANTIC_RELIABILITY_PLAN.md`와 기존 미커밋 구현을 확인하고 보존했다.
  사용자가 이 PC에서는 사내 실제 LLM 검증이 불가능하다고 확정했다. 합성 검증/시험 ZIP과 실제 품질 검증을 구분한다.
  코드 블럭 페이지별 저장, 900초 예산, 체크포인트 재사용/재개 API·UI, 원문 없는 진단 다운로드를 연결했다.
  첫 검증의 명령 옵션/실행 정책·캐시 접근 문제를 수정하고 비동기화 임시 작업본에서 검증 중이다.
  새 이해 토큰 기본값과 기존 낮은 출력 상한의 호환 오류를 보완한 .NET 168/168 통과.
  다음: 재개/취소·큰 입력 회귀, Git 공통 진단/재개 연결, SVG 보안·페인트 검증, 전체 verify 및 시험 ZIP.

- 2026-09-10 의미 분석 신뢰성 개선 구현 시작. 승인 계획은 `SEMANTIC_RELIABILITY_PLAN.md`.
  기준 `17106dd` clean 상태와 internal.4 ZIP을 보존한다. 사내 LLM은 vLLM 입력 20만/출력 6만 토큰,
  Thinking OFF. 입력 10만/100만 자, 요약 우선, 실행 15분/이어하기가 확정됐다.
  다음: SVG 정제와 입력 제한 → 진단/LLM 상호작용 → 부분 저장/재개 → 전체 검증/시험 ZIP.

- 2026-09-10 **internal.4 배포 완료**. 릴리스 커밋 `7ae2a10`을 기존 `origin/main`에
  일반 push했고 `23c2409..7ae2a10`, exit 0을 확인했다. ZIP 크기 권고 외 오류 없음.
  최신 배포는 `artifacts/release/DiagramMaker-0.1.0-internal.4-win-x64.zip`과 SHA 파일이다.
  기본 실행은 `configure-llm.cmd` → `start.cmd`, 추가 허용목록은 전용 실행기로 선택한다.
  구현/전체 verify/패키지 API·UI·LLM 전송 두 모드/실행기/ZIP 검증 모두 완료. 미해결 로컬 실패 없음.
  다음: 사내 비동기화 경로에서 실제 승인 LLM 연결·의미 품질 시험, 필요한 경우 PostgreSQL 검증.
  실제 사내 LLM/DB 검증은 이번 합성 loopback 검사에 포함하지 않았다.

- 2026-09-10 internal.4 최종 검증 완료. 전체 verify exit 0: .NET 168, worker 32, web 45,
  정책 7, 시작 11종, 기본/선택 정책 API와 Edge UI(`code-block-ui-6412sZ`) 통과.
  CMD 6종(`windows-launchers-PXf7KG`)은 프로세스 종료 후 포트 해제 대기로 통과했다.
  패키지/API/UI/LLM 두 모드/ZIP 증적과 verify 로그를 `artifacts/internal4-validation/`에 보존했다.
  배포 ZIP은 `a7b6ee3`, clean 소스, 1,817개/94,119,889바이트이며 SHA-256은
  `6f4ca9c48e5e8e78b1a41fe290b326983b5f4676cace7b2014d388e829c0a62b`다.
  승인된 구버전 ZIP/SHA 10개를 배포 폴더에서 제거하고 internal.4 ZIP/SHA로 교체했다.
  자동 승인 검토의 최초 삭제 거부는 기록된 사용자 승인과 Git 보관/내용 일치 증거로 재검토 후 해소됐다.
  다음: 최종 diff/배포 보고서 검토 → 릴리스 커밋 → 기존 origin/main 일반 push 및 일치 확인.

- 2026-09-10 중단 세션 재개: `a7b6ee3`의 internal.4 패키지 빌드와 ZIP 감사,
  API(`offline-preview-lwwbr9`), UI(`code-block-ui-pUFP61`), 기본/선택 정책 LLM 전송
  (`packaged-llm-wc4eG1`, `packaged-llm-q8DEFN`) 통과 증적을 확인했다.
  남은 실패는 CMD 실행기 검사의 첫 사례 종료 뒤 5080 포트 재사용 오류다.
  기존 실행기 진단 수정은 보존한다. 다음: 실행기 재검증/종료 대기 보완 → 보고서/ZIP 교체 → origin push.

- 2026-09-10 clean worktree 패키지 검사에서 Git autocrlf가 khroma 고지의 줄바꿈을 바꿔
  원본 패키지와 SHA가 달라진 문제를 확인했다. `.gitattributes`로 검토 고지 바이트를 보존한다.
  x64 worker 32/web 45 및 번들 빌드는 통과했다. 다음: 속성 보완 커밋 → clean checkout 고지 비교 → 재빌드.

- 2026-09-10 internal.4 소스 커밋 `f027762` 생성. 비동기화 detached worktree
  `%TEMP%/DiagramMaker-internal4-20260910`에서 동일 커밋/clean 상태로 패키지 빌드 중이다.
  다음: 빌드 결과 확인 → 실제 패키지 API/UI/LLM 두 모드/CMD 실행기/ZIP 감사 → 배포 교체·push.

- 2026-09-10 internal.4 전체 verify exit 0: .NET 168, worker 32, web 45, 정책 회귀 7,
  시작 11종, 기본/선택 정책 API와 Edge UI 통과. UI: `code-block-ui-1k7OGL`.
  검증 중 호출 디렉터리로 인해 라이선스 출력 경로가 OneDrive로 해석되어 중단됐고,
  비동기화 작업본을 실제 작업 디렉터리로 지정한 전체 재실행을 통과했다.
  다음: 검증 소스 커밋 → 동일 커밋의 internal.4 빌드 → 실제 패키지/실행기 검사 → ZIP 교체·push.

- 2026-09-10 internal.4 재개 중: 기본 실행/선택 정책 안내와 API·LLM·시작 회귀를 보완했다.
  .NET 168/worker 32/web 45 통과. 새 PowerShell 테스트의 Node 임시 정리 충돌을 해결했다.
  다음: 전체 verify, 소스 커밋, internal.4 빌드·패키지 검사, 승인된 구버전 ZIP/SHA 정리와 origin push.

- 2026-09-10 사용자 요청으로 실행 설정 간소화와 internal.4 배포를 진행한다.
  기본 실행은 LLM 설정만 사용하고 네트워크 허용목록은 명시적 경로/전용 실행기로 선택한다.
  이전 배포 ZIP/SHA 삭제는 이번 사용자가 명시적으로 승인했으며 과거 보존 지침보다 우선한다.
  다음: 기본/명시적 정책 회귀 → 전체 verify → 구버전 정리 → internal.4 빌드/검증/기존 origin push.

- 2026-09-10 사내 전용 정비 A4와 internal.3 Windows ZIP 검증 완료.
  전체 verify 및 실제 패키지 API/UI/합성 LLM 전송/ZIP 해시 검사가 통과했다.
  기존 checkpoint·작업 변경·과거 ZIP은 보존했다. 소스 커밋은 `5295226`, 패키징 보완은 `6b19242`.
  최신 ZIP: `artifacts/release/DiagramMaker-0.1.0-internal.3-win-x64.zip`.
  검증·설정 안내: `SECURITY_AUDIT_REPORT.md`. 릴리스 커밋 `2d12cd3`의 origin/main push 완료.
  다음: 사내에서 승인된 정책을 설정하고 실제 LLM 품질/연결 및 필요한 PostgreSQL 검증을 수행한다.

- 2026-09-09 사내 전용 보안·라이선스 정비 시작. 새 사용자 승인 기준과 단계별 결과는
  `SECURITY_AUDIT_PROGRESS.md`를 먼저 확인한다. 아래 기록은 정비 전 기능의 이력이다.

- 2026-09-09 **그룹 중심 UI 추가 개선 구현·전체 검증·로컬 서버 적용 완료**.
  필수 제목 placeholder, 구성/결과 탭, 그룹 전용 좌측 목록과 우측 접힘 카드,
  그룹별 사용자 관계/Thinking, 큰 생성 버튼과 주 메뉴 순서를 반영했다.
  빈 그룹 저장·구형 데이터 복원·그룹별 LLM 요청/캐시·관계 적용 제외를 검증했다.
  전체 verify exit 0: .NET 189, worker 30, web 42, API/Fake CLI/Edge UI 통과.
  구성/결과 1440·800·390px 캡처: `artifacts/code-block-ui-5KraDu`.
  기존 5080 서버를 새 빌드로 재시작했고 health 및 실제 제공 JS/CSS 해시를 확인했다.
  현재 미해결 로컬 실패 없음. 기존 checkpoint·미커밋 변경·ZIP은 보존했으며 새 ZIP/커밋/푸시는 만들지 않았다.

- 2026-09-09 추가 계획 **`IMPLEMENT1.txt` 구현·검증·Windows 패키지 완료**.
  미리보기 문자열/구문 구분, 질문 제한, 코드 전용 의미 계획·검증/실패 계약,
  구성/결과 화면·그룹 설정 유지·트리/이력 복원과 합성 LLM 회귀 검증을 완료했다.
  최종 패키지: `artifacts/release/DiagramMaker-0.1.0-cb.2-win-x64.zip`.
  실제 내부 LLM 연결은 이번 검증에서 제외했다. checkpoint·기존 ZIP·미커밋 변경을 보존했다.

- 기준 브랜치: `main`, 원격: `origin` (기존 GitHub 저장소).
- 구현 전 HEAD: `58d703f` (Promote offline package to version 15).
- 기존 작업 checkpoint: **`e53efc2`** (82 files). 의미 기반 Git 다이어그램/설명/근거 UI,
  격리 Codex 합성 샘플 테스트, offline.16 패키지, 계획/인계 문서 포함.
- `git push origin main` 완료: `58d703f..e53efc2 main -> main` (exit 0).
- 기본 기능 단계: **P6 로컬 검증·패키징 완료**. 코드 블럭 기능 구현, 전체 verify, 추가 Edge UI,
  실제 Windows 패키지 검증 통과. 실제 내부 LLM 품질 및 PostgreSQL 연결 검증은 환경 미제공으로 남았다.
- 새 기능 변경은 작업 트리에 있으며 커밋/푸시하지 않았다. 기존 checkpoint와 offline.16 ZIP은 보존했다.

## 단계별 체크리스트

- [x] P0 기존 변경 검토, 전체 검증, checkpoint commit/push 확인
- [x] P1 계약/설정/3종 저장소/소유자 검증/API/취소/lease 및 단위 테스트
- [x] P2 C#/C++ 코드 조각 분석/원본 위치/호출 후보/상태 전이 및 테스트
- [x] P3 그룹 제안/질문/사용자 관계/답변 무효화 및 테스트
- [x] P4 의미 이해/추천/5종 생성/근거/상세 페이지/재생성 및 테스트
- [x] P5 새 탭/그룹/질문/결과/편집/이력 화면 및 UI 검증
- [x] P6 전체 verify, 문서, UI/API 및 Windows 패키지 검증, 외부 검증 상태 기록
- [x] IMPLEMENT1 I1 미리보기 안전 검사·질문 제한·의미 계약/검증과 실패 구분
- [x] IMPLEMENT1 I2 구성/결과 화면·그룹별 옵션·결과 트리/생성 이력 복원
- [x] IMPLEMENT1 I3 합성 LLM 실패 회귀·URL 라벨 5종·키보드/복원 UI 검증
- [x] IMPLEMENT1 최종 전체 verify·새 Windows 패키지 및 최종 화면 확인
- [x] 그룹 UI 개선: 필수 제목·탭·그룹/카드·그룹 옵션·생성 버튼·메뉴 순서
- [x] 그룹 UI 개선: 계약/저장/호환/LLM·캐시 회귀, 전체 verify, 화면 검토, 로컬 서버 재시작
- [ ] 승인된 실제 내부 LLM 합성 C/C++/C# 의미 품질 검증 및 실제 PostgreSQL 연결 검증

## 검증 기록

- 2026-09-08 계획 단계: 기존 C++ 파서로 C 함수/교차 블럭 호출을 확인했다.
  함수 내부 구문만 입력하면 오류 없이 symbols=[]가 될 수 있어 조각 보정/누락 검사가 필수다.
- 2026-09-08 P0: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1`
  직접 실행 **exit 0**. .NET 142, Git worker 21, web 12 tests 통과. frontend build,
  npm audit(취약점 0), license, API smoke, 격리 Fake CLI sample smoke 통과.
  API fixture: `artifacts/api-smoke-TGJf7t`; sample log: `artifacts/codex-smoke-QTAAI0/server.log`.
  `artifacts/code-block-baseline-verify.log`는 앞선 PowerShell 리다이렉션 시도의 부분 로그다.
  출력 리다이렉션은 native stderr 진행 메시지를 오류로 처리하므로 이후 verify는 직접 실행한다.
- P0 커밋 대상 82파일 자격증명 패턴 검사: 발견 없음. offline.16 ZIP 1,554 entries에서
  예상 밖 data/repositories/.git/auth/실제 LLM policy 경로 없음. SHA-256 파일 일치.
- 계획 문서와 진행 기록을 루트에 생성했다.

## 재개 지점

1. `git status --short`와 이 파일을 확인한다. checkpoint는 이미 origin/main에 저장됐다.
2. 현재 상태 맨 위와 `SECURITY_AUDIT_PROGRESS.md`, `SECURITY_AUDIT_REPORT.md`를 확인한다.
   현재 배포는 internal.5이며 기본 실행에는 LLM 설정만 필요하다. 구현/전체 verify/패키지 검사는 완료했다.
   실행기 포트 해제 문제도 해결됐다. 커밋/푸시 여부는 맨 위의 최종 기록과 Git 상태로 확인한다.
   구버전 ZIP/SHA는 사용자 승인으로 배포 폴더에서 제거했으며 Git 이력은 보존했다.
   같은 구현/검증을 반복하지 않는다. 다음 제품 검증은 승인된 사내 LLM 연결/의미 품질 시험이다.
   실제 내부 LLM 및 필요한 PostgreSQL 환경 검증은 별도 범위이며 환경이 제공되면 수행한다.
3. 계획과 실제 구현 차이가 생기면 근거와 호환 영향을 기록한다.
4. 각 단계의 실제 파일/검증 명령/결과/남은 작업을 아래 변경 이력에 기록한다.

## 변경 이력

- 2026-09-10 원격 배포 완료: `git push origin main` exit 0, `e53efc2..2d12cd3 main -> main`.
  최초 자동 승인 검토는 GitHub 반출 대상의 승인 증적 부족으로 거부했다. 기존 origin URL과
  원격의 이전 소스/ZIP 배포 이력을 확인해 재검토를 승인받았다. 우회 또는 강제 push는 없었다.
  GitHub는 ZIP 크기 권고 경고를 출력했지만 세 ZIP 모두 정상 수신했다.
  코드·ZIP·보고서 배포는 완료했으며 다음 작업은 사용자의 사내 실연결 시험 결과에 따른 후속 대응이다.

- 2026-09-10 internal.3 패키지 최종 통과: 소스 `6b19242`/clean, 1,816 파일, 94,119,000 bytes.
  SHA-256 `fd7bc703dab5879711098662f61c1ac73950ac4fbd1efe170e562bc50b850068`.
  패키지 API/5종/C++/Mermaid `offline-preview-IgdXfH`, UI `code-block-ui-bmv0yA`,
  합성 LLM 전송/실패 거부 5종 `packaged-llm-GjKh3r`, ZIP 전체 바이트/고지 감사 통과.
  실제 사내 LLM/DB는 환경 미제공으로 미실시이며 사내 테스트 절차를 최종 보고서에 남겼다.
  남은 작업: ZIP과 보고서 커밋, 일반 push와 원격 SHA 확인.

- 2026-09-10 사내 전용 정비 최종 회귀 통과: .NET 163, worker 32, web 44, 정책 회귀 6,
  API/Edge UI, 시작 거부 5종, npm/NuGet 고지 해시와 SBOM 검사 통과.
  삭제된 외부 공급자 테스트로 .NET 개수가 감소했으며 코드 블럭/내부 LLM 회귀는 유지했다.
  새 패키지 LLM 전송 검사 5종도 합성 loopback 서버로 통과했다. 실제 사내 LLM/DB는 미실시.
  다음: 소스 커밋과 internal.3 최종 패키지 검증, 보고서 및 Git push.

- 2026-09-09 그룹 UI 최종 완료: `verify.ps1` exit 0. .NET 189/189, worker 30/30,
  web 42/42, frontend build, npm audit 취약점 0, 라이선스 검사, API
  (`artifacts/api-smoke-ssk7x7`), Fake CLI (`artifacts/codex-smoke-uBOdim`), Edge UI
  (`artifacts/code-block-ui-5KraDu`) 통과. 합성 LLM으로 그룹별 4단계 Thinking 전달,
  선택 재생성 옵션 유지·캐시 분리·다른 그룹 결과 재사용을 검사했다.
  InMemory/LocalFile 빈 그룹·비활성 미완성 관계 저장/복원, 활성 관계 생성 검사 통과.
  `start-local.ps1 -NoBrowser -Port 5080` exit 0, health=healthy, 실제 제공 app.js/app.css가
  web/dist 빌드 SHA-256과 일치. 기본 verify의 기존 PID 7996 잠금 실패는 서버 중지 후 해결했다.
  `git diff --check` 통과. 실제 내부 LLM/PostgreSQL 연결은 이번 범위에서 실행하지 않았다.
  다음: 새 사용자 피드백 또는 별도 배포/실연결 검증 요청에 따라 후속 작업. 미해결 로컬 실패 없음.

- 그룹 UI Edge 스모크 통과: `artifacts/code-block-ui-2L5w9Z`. 제목 필수/포커스,
  탭 키보드/스타일, 그룹별 옵션·빈 그룹 저장·이동·관계 적용 제외, 기존 결과 탐색/편집/
  다운로드·질문 병합·5종 라벨을 확인했다. 구성 1440/800/390px 캡처와 페이지 폭 검증 통과.
  1440/390px 화면 직접 확인 완료. 전체 verify 첫 실행은 기존 로컬 API PID 7996의
  DLL 잠금으로 실패했다. data/local-processes.json과 시작 시각을 대조한 개발 서버를
  공식 stop-local로 중지한 뒤 verify를 재실행하며, 끝나면 5080에서 다시 시작한다.
  다음: 전체 verify 결과 확인 → 진행 기록 마감 → 로컬 서버 재시작/health 확인.

- 2026-09-09 그룹 UI 개선 1단계: 필수 제목·전용 탭·그룹 목록·접힘 카드·하단 생성 버튼·
  주 메뉴 순서를 구현했다. 그룹 옵션 계약, 빈 그룹 저장, 명시적 그룹 기본값,
  활성 관계 필터, 그룹별 LLM 옵션/캐시와 선택 재생성·병합 설정 보존을 연결했다.
  코드 블럭 .NET 47/47, web 42/42 및 TypeScript 통과. 처음 별도 artifacts-path 테스트는
  해당 경로의 복원 자산이 없어 실패해 기존 obj를 사용하는 별도 출력 경로로 수정했다.
  기본 샌드박스에서 esbuild 상위 경로 접근이 거부됐으나 동일 빌드 확장 권한 실행은 통과했다.
  다음: 확장 Edge UI 검증과 화면 검토 → 전체 verify. 실 LLM/DB 연결은 수행하지 않는다.

- 2026-09-09 IMPLEMENT1 최종 완료: 실제 `0.1.0-cb.2` 패키지 EXE/내장 x64 Node/wwwroot로
  `smoke-offline-preview.mjs` exit 0 (`artifacts/offline-preview-9DBvPw`): 코드 5종/7페이지,
  C/C++ stdin 분석, 근거/질문/편집/삭제와 기존 프리셋 4종 × 4개 화면 폭, 배포 자산 해시 일치.
  `smoke-code-block-ui.mjs` 패키지 모드 exit 0 (`artifacts/code-block-ui-lJiMwH`): URL/click 5종,
  키보드/접힘/화면·위치/과거 생성·편집 복원, 질문·그룹 구성, SVG/PNG, 3개 화면 폭 모두 통과.
  390px 캡처에서 최종 도구 모음 줄바꿈도 직접 확인했다.
  ZIP 감사 `artifacts/code-block-cb2-package-audit.json` 통과: 1,555 entries, 93,801,309 bytes,
  필수 런타임·UI·파서 포함/소스 일치, 런타임 원문·저장소·인증·실제 정책 경로 없음, 체크섬 일치.
  SHA-256: `319a79c16c2c4e866dee2563f71610b3601e17e29e40ce043b055ce9b767ab9a`.
  이전 cb.1 ZIP 해시는 그대로다. 최종 CSS는 전체 verify 뒤 패키지 빌드/전체 패키지 UI로 검증했다.
  `git diff --check` 통과. 현재 미해결 로컬 실패 없음. 새 커밋/푸시는 하지 않았다.
  다음: 승인된 내부 LLM/실제 PostgreSQL 환경이 제공되면 별도 실연결 검증과 결과 기록.

- `build-offline-win-x64.ps1 -Version '0.1.0-cb.2' -SkipTests` exit 0. x64 Node worker 30/30,
  web 38/38, 프런트엔드 빌드·audit·라이선스·self-contained 게시·ZIP 생성 통과.
  UI 스모크에 `artifacts/stage`의 패키지 경로 인수를 추가해 실제 패키지 EXE/Node/wwwroot를 검증한다.
  테스트 데이터와 LLM 비활성 정책은 별도 fixture에 둔다. 다음: 두 패키지 스모크 완료 및 최종 화면 확인.

- IMPLEMENT1 최종 `verify.ps1` exit 0: .NET 185, worker 30, web 38, 빌드·audit(취약점 0)·
  라이선스·API(`artifacts/api-smoke-Xp33Uc`)·가상 CLI(`artifacts/codex-smoke-c1GEv9`)·
  Edge UI(`artifacts/code-block-ui-wXDZU1`) 모두 통과.
  화면 검토 후 작은 창의 도구 모음 버튼이 세로로 눌리지 않도록 줄바꿈 CSS를 추가했다.
  `0.1.0-cb.2 -SkipTests` 패키지 빌드 시작(.NET 재검사만 생략, x64 worker/web 테스트는 실행).
  다음: 패키지 API/파서/프리셋 스모크와 패키지 UI 전체 검증으로 최종 CSS까지 확인하고 ZIP 해시를 기록한다.

- IMPLEMENT1 I3 확장 UI 통과: `artifacts/code-block-ui-zhEkZL`. URL/click 라벨 5종, 링크 동작 제거,
  방향키/Home/End/Space/Enter, 화면 전환·새로고침 시 트리 접힘과 선택 위치 복원,
  이전 생성본과 수동 편집 리비전 복원, 탐색 중 생성 요청 없음, 근거/다운로드/질문/병합/분리 통과.
  중간 재검증(`artifacts/code-block-ui-Kbvs0M`)에서 State도 같은 Markdown URL 누락을 보여
  Class/State의 표시 문자열에만 보정을 적용했다. 다음: 최종 전체 verify와 별도 `0.1.0-cb.2` 패키지 검증.

- IMPLEMENT1 재개 검증: .NET 185/185, worker 30/30, web 34/34, 빌드·audit(취약점 0)·라이선스·
  API(`artifacts/api-smoke-aR0EYk`)·가상 CLI(`artifacts/codex-smoke-aieCiB`) 통과 후 UI 5종 검사에서 실패했다.
  `artifacts/code-block-ui-jMoSgH`의 Class 그림에서 URL이 사라졌다. 로컬 Mermaid 코드와 원본 SVG로
  SVG 정제 이전 Markdown 자동 링크 토큰이 누락되는 것을 확인했다. 보안 검사 뒤 표시 URL에만
  임시 폭 없는 문자를 넣고 SVG 텍스트에서 제거하여 원래 URL/DSL/strict 정책을 보존한다.
  키보드 전체 이동·접힌 트리 복원·과거 생성/편집 복원·탐색 중 재생성 없음 검증을 추가했다.
  수정 후 web 37/37 및 빌드 통과. 다음: 확장 UI 검증을 통과시키고 전체 verify를 마무리한다.

- 2026-09-09 IMPLEMENT1 I3 재개: 계획·진행 기록·작업 트리를 확인했다. 기존 checkpoint와 모든 변경을 보존한다.
  Browser runtime의 가용 목록이 비어 있어 기존 격리 Edge 스모크를 사용한다.
  첫 `verify.ps1`은 샌드박스의 NuGet 캐시/네트워크 제한으로 복원 실패(NU1801/NU1101/NU1102).
  같은 명령을 확장 권한으로 재실행하여 복원·빌드는 통과했고 전체 검증을 진행 중이다.
  다음: 미완료 UI 확장 검증 결과를 확인하고 결과 트리/이력/그룹 설정 회귀를 보완한다.

- IMPLEMENT1 I3 검증: 코드 블럭 .NET 43/43, web 31/31, 프런트엔드 빌드 통과.
  Guard의 403 응답/반환 2개 묶음, 모든 판단과 원본 사실, 경계 초과 요약 거부,
  근거 누락/분기 반전/허위 관계/잘못된 JSON/null/시간 초과와 1회 수정 한도를 검증했다.
  조건 사실의 Statement가 조건을 둘러싼 if 본문까지 중복하던 것을 원본 조건 범위로 수정하고
  코드 분석 캐시를 v2로 올렸다. 함수 전체 원문은 별도 문맥에 보존한다.
  Browser는 연결된 인스턴스가 없어 문서/목록 확인 후 기존 격리 Edge 스모크로 검증했다.
  UI 첫 통과: artifacts/code-block-ui-MrSqI0 (구성/결과, 정적 분리, 근거, 편집, 다운로드,
  질문 복원/답변, 병합/분리, 3개 화면 폭). 다음: URL 라벨 5종과 키보드/위치 복원 확장 검사 및 전체 verify.

- 2026-09-09 IMPLEMENT1 I1/I2: 미리보기 구문/표시 문자열 분리, 실제 질문 후보 기준 제한,
  코드 전용 v2 의미 계약·연속 처리 검증, 실패 단계와 페이지 분류/대상 필드, 단일 함수 중복 제거 구현.
  그룹 구성/선택 편집기/출력 드롭다운, 결과 트리/키보드/생성 이력/URL 복원과 정적 구조 별도 열기 연결.
  Mermaid strict/SVG 정제와 기존 API를 유지한다. 새 가상 응답은 테스트 전용이다.
  검증: 보안 단위 16/16, 코드 파이프라인 9/9, TypeScript 통과.
  기본 .NET 빌드는 기존 로컬 서버 PID 31876의 DLL 잠금으로 실패하여 artifacts/implement1-tests 출력으로 통과.
  Browser 연결을 확인 중이다. 다음: GuardAsync/의미 병합·실패 회귀 추가, 새 UI 스모크 수정, 전체 verify.

- 2026-09-08: 실행 승인 수신. 계획/진행 기록 생성. baseline 검증 시작.
- 2026-09-08: baseline 검증 완료(exit 0). checkpoint 커밋/원격 push 준비 완료.
- 2026-09-08: checkpoint `e53efc2` 생성. origin/main 일반 push 진행 중.
- 2026-09-08: checkpoint push 성공(exit 0). 새 기능 P1 구현 시작.
- 2026-09-08 P1: `CodeBlockContracts`, `CodeBlockOptions`, `ICodeBlockStore`, 3개 저장소의
  CodeBlocks partial, `CodeBlockWorkspaceService`, `CodeBlockEndpoints` 및 Program 연결 추가.
  입력 변경의 원자적 실행 취소, lease token/CAS, 소유자/출처/입력 한도 검증,
  원문/실행/편집 삭제, 로컬 원자 파일 쓰기/중단 복구, Postgres 트랜잭션/인덱스 포함.
- P1 검증: solution build 성공(경고 0), `dotnet test ... --filter FullyQualifiedName~CodeBlockStoreTests`
  8/8 통과(InMemory/LocalFile 동시 생성, stale worker, lease 교체, 원문/질문 복원, 삭제, ACL).
  Postgres 실제 연결 검증 및 HTTP smoke는 P6에서 수행 필요.
- 공통 DiagramEdge.RelationOrigin / DiagramExplanation.Behaviors 선택 필드 추가(구형 결과 역호환).
- P1 후속 확인: Postgres 편집 저장과 workspace 삭제의 경쟁 조건 검증/보완 필요.
- 2026-09-08 재개: checkpoint와 기존 변경을 보존하고 `CodeBlockAnalyzer`, stdin 전용 C++ 작업자,
  원본 UTF-16 위치/해시/근거, 실제 타입·호출·상태 전이 및 조각 보정을 연결했다.
  solution build 경고/오류 0, C# 분석 테스트 5/5, Node 코드 블럭 분석 테스트 5/5 통과.
  동명/오버로드/수신 객체/파일 지역 함수는 자동 추정 연결을 제한한다.
  다음: P3 그룹/질문 테스트, P4 의미 생성/5종 투영/lease 작업자, P5 화면, P6 전체 검증.
- P3/P4/P5 연결: 그룹 연결 성분·최대 5개 질문·사용자 관계 출처, 코드 전용 의미 이해/계획/검토,
  5종 투영, 원본 근거/상세 페이지, lease 갱신 작업자, 새 React 탭/저장/그룹/질문/결과/편집/이력 구현.
  API 스모크에서 5종/C++/불변 원문/질문 재개/편집 충돌/삭제 및 기존 Git 4종 경로 통과.
  LLM 비활성 결과는 Partial/Incomplete로 명시한다. 실제 사내 LLM 품질 검증과 구분한다.
- 프런트엔드 tsc 통과. esbuild 최초 실행은 샌드박스 상위 경로 접근 제한으로 실패했고,
  승인된 확장 권한 실행으로 번들 빌드 통과. 다음: 도메인/의미 회귀, UI 확인, 전체 verify, 패키지 확인.
- 첫 전체 `verify.ps1` exit 0: .NET 165, Node worker 27, web 12 tests, frontend build,
  npm audit 취약점 0, license allowlist, 코드 블럭 API/기존 Git API/Fake CLI sample smoke 모두 통과.
  API fixture `artifacts/api-smoke-4MM3nL`, sample log `artifacts/codex-smoke-AJPCZZ/server.log`.
- 브라우저 연결 도구에 사용 가능한 브라우저가 없어 기존 선택형 Headless Edge 검증 스크립트를 확장했다.
  UI 첫 시도는 서버가 기존 web/dist를 우선 제공해 구버전 탭을 확인하는 테스트 환경 오류로 실패했다.
  격리 API/wwwroot 복사본을 제공하도록 수정했고 재검증 중이다. 스크린샷/진단은 artifacts/code-block-ui-*.
- 승인된 실제 내부 LLM policy는 현재 환경에 없으며 설정 경로를 사용자에게 비동기로 요청했다.
  응답 전까지 합성 계약/실패 검증만 수행한다. 실제 LLM 품질 성공으로 기록하지 않는다.
- UI 재검증 통과: `artifacts/code-block-ui-jCurAV`에 Flow/Sequence/관계도 및 화면 폭별 PNG 저장.
  탭 초안 유지, 언어, 블럭 순서, 종류 선택, 생성, 확대, 새로고침 복원 검증. 호출 근거의 같은 줄
  C++ 위치 누락을 찾아 공통 CFG에 선택 offset을 추가하고 새 코드 경로에서 소비하도록 보완했다.
  그룹 표시의 내부 GUID를 제거하고 저장/조회 중 초안 편집을 차단했다.
- 2026-09-09 재개: 직전 C++ 상속 테스트 실패는 같은 줄의 여러 선언을 줄 번호만으로 매칭해
  첫 선언으로 잘못 연결한 문제였다. 선언/CFG의 UTF-16 offset을 사용하도록 보완했다.
  Unicode 매개변수 이름·기본값의 타입 정규화에서 UTF-8/UTF-16 혼용도 수정하고 회귀를 추가했다.
  다음 명령: 전체 verify → 새 버전 offline 패키지 빌드 → 패키지 내부 UI/C++ worker 스모크.
- C++ 상속/같은 줄 선언/Unicode 타입 정규화 회귀 21/21 통과. 공통 분석 캐시는
  `source-graph-v9`로 갱신하여 이전 잘못된 Unicode 심벌 캐시를 재사용하지 않도록 했다.
  기존 Git 공개 API는 유지된다. 코드 블럭 분석/프롬프트는 별도 v1 키를 사용한다.
- 최신 verify exit 0: .NET 165, Node worker 30, web 12, API·sample API 차단·Edge UI 통과.
  UI 캡처 `artifacts/code-block-ui-kLz2Ye`, API fixture `artifacts/api-smoke-11P5hT`.
- 첫 패키지 `0.1.0-code-block-preview.1`은 빌드/테스트/게시까지 통과했으나 긴 Windows
  라이선스 경로 복사에서 실패했다. 버전명을 짧은 `0.1.0-cb.1`로 바꿔 재생성한다.
  기존 offline.16 ZIP과 체크포인트는 유지한다. 최종 검토에서 의미 재생성 실패도 이전 의미 성공
  페이지를 보존하도록 보완하고 선택 재생성/수동 관계 출처 회귀를 추가했다.
- 재개 후 새 편집 회귀의 `DiagramEditDocument` 생성자 인수 순서를 수정했다. 코드 파이프라인
  테스트 9/9 통과: 선택하지 않은 결과 유지, 의미 재생성 실패 시 이전 성공 페이지 유지,
  수동 관계 변경 시 코드 출처/근거 제거 및 사용자 제공 표시를 확인했다.
  다음: 최신 전체 verify → `0.1.0-cb.1` 패키지 → 실제 패키지 UI/API/작업자 검증.
- 전체 verify exit 0: .NET 166, Node worker 30, web 12, API/Fake CLI/Edge UI 모두 통과.
  API `artifacts/api-smoke-FA2JSH`, sample `artifacts/codex-smoke-2PV62S`, UI `artifacts/code-block-ui-vat2jk`.
  추가 UI 검증에서 수동 편집 저장 후 SVG 다운로드 내용 확인이 실패했다.
  진단 `artifacts/code-block-ui-v3eVQF`: 렌더링 대기/내보내기 내용을 확인하고 질문·병합/분리까지 검증한다.
- 추가 UI 검증 exit 0: `artifacts/code-block-ui-vTS57h`. SVG는 정상이며 단어별 `<tspan>`을
  문자열 검사에서 고려하지 않은 테스트 오류를 수정했다. 원본 근거, 수동 관계 출처, SVG/PNG
  다운로드, 질문 대기 새로고침 복원, 답변 적용/명시적 그룹 병합, 독립 그룹 분리/저장을 확인했다.
  최신 Flow 스크린샷에서 조건/반환/실제 save 호출 연결도 확인했다. 다음: 짧은 버전명으로 패키징.
- `build-offline-win-x64.ps1 -Version '0.1.0-cb.1'` exit 0. 166/30/12 테스트와
  x64 Node 작업자, 프런트엔드, .NET self-contained 게시, 라이선스 수집/ZIP 압축 통과.
  `artifacts/release/DiagramMaker-0.1.0-cb.1-win-x64.zip` 생성. 다음: 패키지 스모크와 ZIP/SHA-256 확인.
- 패키지 첫 스모크는 C# 5종/기존 프리셋 통과 후 C++ 실행에서 실패했다(`artifacts/offline-preview-rC4az0`).
  테스트가 실행 파일을 직접 시작하면서 `start.cmd`의 패키지 Node/작업자 경로 환경변수를 누락했다.
  실제 실행기와 같은 패키지 경로를 지정하도록 테스트를 수정했다. 패키지 파일 변경은 없다.
- 최종 패키지 스모크 exit 0: `artifacts/offline-preview-DwoGB1`. 실제 패키지의 내장 x64 Node로
  C++ stdin 분석, C# 5종 API, 근거/질문/편집/삭제 통과. 5종 결과 7개 페이지를 패키지 Mermaid로
  렌더링했고 기존 프리셋 4종 × 4개 화면 폭 검사도 통과했다. 외부 연결/실제 LLM 호출은 없었다.
- ZIP 감사 통과: `artifacts/code-block-package-audit.json`, 1,555 entries, 93,784,572 bytes.
  필수 작업자/런타임/UI 포함, 원문 data/저장소/.git/auth/실제 LLM policy 경로 없음,
  패키지 parser와 원본 일치, SHA-256 파일 일치.
  SHA-256: `febf1a5b8fa1a9df5a8f0bb3d3a168ffa329a90fe61dfd583e97b07df10eaa6f`.
  manifest는 `sourceCommit=e53efc2...`, `sourceTreeDirty=true`로 미커밋 미리보기임을 명시한다.
- `git diff --check` 통과. 최신 전체 verify 뒤 변경한 테스트 스크립트는 추가 Edge UI와 패키지
  스모크에서 직접 검증했다. 현재 해결되지 않은 로컬 빌드/회귀/패키지 실패는 없다.
- 남은 검증: 승인된 내부 LLM policy 경로가 없어 실제 모델 품질은 미실시(이미 사용자에게 경로 요청).
  PostgreSQL 구현은 빌드/정적 검토까지 수행했으나 실제 DB 서버/클라이언트가 없어 실연결·동시성 검증은
  미실시다. InMemory/LocalFile 회귀를 PostgreSQL 실연결 검증으로 간주하지 않는다.
  다음: 환경이 제공되면 해당 두 검증을 수행하고 결과를 여기에 추가한다. 신규 커밋/푸시는 수행하지 않았다.
