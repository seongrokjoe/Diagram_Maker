# 코드 블럭·자연어 다이어그램 진행 기록

다음 작업은 `AGENTS.md`, `CODE_BLOCK_DIAGRAM_IMPLEMENTATION_PLAN.txt`, 이 파일을 먼저 읽는다.
전체 제품 설명과 사용법은 `README.md`, 보안 기준은 `SECURITY.md`를 따른다.

## 현재 배포 상태

- 최신 사내 시험 배포: `0.1.0-internal.12` (internal.11 ZIP 보존).
- 배포 파일: `artifacts/release/DiagramMaker-0.1.0-internal.12-win-x64.zip` 및 SHA-256 파일.
- ZIP 크기 96,495,442바이트, 1,832파일.
- SHA-256: `ce2b04aa4b2d7a7c73ae73a72a840c4233708f6b7f9e63ce46ec691b7ddb1a5f`.
- 기준 커밋은 `516a579`이며 internal.12 변경은 그 이후 작업이다.
- 최신 변경·검증 결과: `NATURAL_DIAGRAM_RECOVERY_REPORT.md`.
- 코드 블럭 기능 구현 전 checkpoint `e53efc2`와 기존 사용자 변경은 Git 이력에 보존되어 있다.

## internal.11: 자연어 다이어그램 안정성 개선

승인 범위와 최종 동작은 `NATURAL_DIAGRAM_RELIABILITY_PLAN.md`, 검증 결과와 한계는
`NATURAL_DIAGRAM_RELIABILITY_REPORT.md`에 기록했다.

- 서버 발급 원문 근거 ID와 UTF-16 범위, 복수 근거, 마스킹 전후 위치 연결.
- 요구사항 추출·검토, 공유 시나리오 계획, 형식별 설계·보정, 형식 간 최종 검토.
- 최초 생성 뒤 최대 두 차례 보정, 정상 항목 보존, 출력 한도 시 상세 페이지 분할.
- 최대 5개 질문의 저장·답변·재시작 복원과 revision·질문 버전·소유자 ACL 검증.
- 실행 체크포인트·진단 다운로드, 선택 상세 재생성, 폴링 장애 복구.
- InMemory/LocalFile/PostgreSQL 계약의 lease·취소·질문 대기·재개 일치.
- 새 생성 버전 `natural-v7`, 설계 계약 `natural-design-v3`.

## internal.11 최종 검증

2026-09-21 전체 검증과 internal.11 패키지 검증을 완료했다.

- 전체 verify exit 0: .NET 370, 작업자 43, 웹 62, 정책 13, 시작 정책 11.
- API 두 모드, 공유 의미 기본/60,000자 한도, SVG 6, Sequence 4 통과.
- 코드·디자인·자연어 UI와 현재 소스 CMD 14개 통과.
- 패키지 API/UI, 합성 LLM 두 모드, 공유 의미 두 한도, Windows CMD 7개 통과.
- 자연어 합성 회귀: 51회 요청, 근거·보정·분할·질문·재시작·ACL·390/1440px 확인.
- `git diff --check` 통과. 미해결 로컬 검증 실패 없음.

사내 실제 LLM의 의미 품질·응답 시간, PostgreSQL 실연결·동시성, 호스트 egress 캡처,
외부 취약점 피드는 검증하지 않았다. 합성 검증으로 이 항목을 대체했다고 간주하지 않는다.

## 재개 지점

### 2026-09-21 internal.12 수정 시작

- 승인: 자연어 추출/검토 보정 분리, 상세 실패 원인과 복구 마감, 사내용 자연어 검사, 로컬 검증·ZIP·커밋·푸시.
- 시작 기준 `516a579`; 작업 트리 깨끗함. internal.11 ZIP과 기존 checkpoint 보존.
- 확인된 결함: 검증 기록까지 Requests로 집계, null 진단을 unknown으로 표시, 자연어 Retrying 미마감,
  검토 JSON 오류 시 추출 재실행, 시나리오 실패 진단 누락 및 보정 ID 병합 충돌.
- 실제 사내 최초 검증 코드/모델 응답은 미제공. 로컬 재현·회귀와 실제 사내 검증을 구분한다.
- 다음: 자연어 단계별 검증/보정 및 진단을 수정하고 loopback 회귀를 추가한다.
- 1차 구현: 추출/검토 독립 보정, 원문 ID 수정 범위 해제, 상세 근거/검토 오류,
  로컬 스키마 제약 및 호환 전송, 진단 종료 상태·JSON API·화면 연결, 사내 합성 검사 API/UI/CMD 추가.
- 비동기화 검증 사본 `Temp/DiagramMaker-natural-internal12`에서 관련 .NET 97개 통과.
  신규 14개 회귀는 HTTP 3회 뒤 근거 실패, 검토 응답 7종, 재개 시 횟수 보존,
  원문 ID 수정·정상 항목 보존·시나리오 오류·ID 변경·호환 스키마 로컬 길이 검증을 포함한다.
- 첫 검사에서 PowerShell 실행 정책으로 offline 환경 설정이 적용되지 않았다. 프로세스 범위
  Bypass를 적용한 뒤 사전 반입 캐시 복원 및 빌드 성공. 외부 의존성 다운로드 없음.
- 다음: 실제 loopback API/UI와 사내용 합성 검사 회귀 → 전체 verify → 패키지 검증.
- 재개 검증: 최신 변경 4개를 비동기화 사본에 반영했다. esbuild 상위 폴더 읽기 제한은
  확장 권한으로 오프라인 빌드를 실행해 해결했다. 관리자 검사 API의 비관리자 요청이
  인증 서비스 부재로 500이 되던 오류를 명시적 403 응답으로 수정했다.
- loopback API/Edge 회귀 통과 (`natural-reliability-3eMvo3`): 독립 검토 보정, 소진 마감,
  HTTP 중단 재개, 스키마 호환, 합성 검사 5개 뷰와 관리자 ACL, 전체 오류 목록,
  소진 시 이어하기 숨김, 검사 보고서 다운로드 및 390/1440px 화면을 확인했다.
  화면 회귀의 실패 이력 선택 누락은 실제 최근 실행 버튼을 선택하도록 수정했다.
- 다음: 전체 verify 종료 확인 → internal.12 ZIP/패키지 검사 → 보고서·커밋·일반 푸시.
- 전체 검증 1회차에서 .NET 384·작업자 43·웹 62·정책 13개 통과 후 PowerShell의
  `*>` 로그 수집이 빌드 stderr를 NativeCommandError로 바꿔 중단했다. 제품 결함과 구분하며,
  CMD의 stdout/stderr 리디렉션으로 전체 검증을 다시 실행 중이다. 실행/빌드/테스트 301개
  파일이 원본과 검증 사본 사이에서 SHA-256으로 일치한다.
- 전체 verify 최종 exit 0: .NET 384, 작업자 43, 웹 62, 정책 13, 시작 정책 11,
  API 두 모드·공유 의미 두 한도·SVG 6·Sequence 4·코드/디자인/자연어 UI·CMD 14개 통과.
  자연어 최종 소스 회귀 `natural-reliability-Ha1PTW`, 실행기 `ui-test-launchers-00x4dU`.
  배포 보고서의 Build 값에 실제 패키지 버전이 들어가도록 publish Version을 지정했다.
- 다음: internal.12 패키지 검사 9개와 ZIP/SHA 대조, 보고서 마감 후 커밋·일반 푸시.
- internal.12 빌드 exit 0. ZIP 96,495,442바이트·1,832파일의 stage 대비 SHA-256 대조 통과.
  SHA-256 `ce2b04aa4b2d7a7c73ae73a72a840c4233708f6b7f9e63ce46ec691b7ddb1a5f`.
  원본 소스와 검증 사본 대조 후 manifest에 기준 커밋과 dirty 상태를 반영했다.
  패키지 API/미리보기 통과, 나머지 패키지 회귀 실행 중.
- 패키지 검사 9개 모두 exit 0: API/미리보기, 코드 UI, 디자인 UI, 자연어 API/UI,
  합성 LLM 두 모드, 공유 의미 두 한도, Windows CMD 8개. 자연어 `natural-reliability-qipoCe`,
  공유 의미 `shared-semantics-FdTZvB`/`shared-semantics-VZ9uVC`, CMD `windows-launchers-JVoVhy`.
  최종 실행/빌드/테스트 파일 301개의 원본/사본 SHA-256 일치. 로컬 미해결 실패 없음.
- 다음: 소스·배포·문서 커밋과 일반 푸시. 실제 사내 모델의 합성 검사 및 기존 실패 입력은
  사내 환경 제공 후 수행한다. 결과는 `NATURAL_DIAGRAM_RECOVERY_REPORT.md`에 추가한다.

internal.12 구현·로컬 검증·패키징은 완료됐다. 다음 작업은 사내 환경이 제공되면 실제 LLM과
PostgreSQL을 검증하고 결과를 이 파일과 최신 보고서에 추가한다. 새 기능을 시작할 때는 기존
checkpoint와 사용자 변경을 보존하며, 의미 있는 구현·검증·중단 뒤 이 파일의 현재 상태를 갱신한다.
