# 코드 블럭·자연어 다이어그램 진행 기록

다음 작업은 `AGENTS.md`, `CODE_BLOCK_DIAGRAM_IMPLEMENTATION_PLAN.txt`, 이 파일을 먼저 읽는다.
전체 제품 설명과 사용법은 `README.md`, 보안 기준은 `SECURITY.md`를 따른다.

## 현재 배포 상태

- 최신 사내 시험 패키지: `0.1.0-internal.13` (현재 배포 폴더에는 최신 ZIP/SHA만 유지).
- 배포 파일: `artifacts/release/DiagramMaker-0.1.0-internal.13-win-x64.zip` 및 SHA-256 파일.
- ZIP 크기 96,508,426바이트, 1,832파일.
- SHA-256: `9778ffbeefec6f390fa56e62831a980ecc615a80edec153ca4cc8173e2790edd`.
- 기준 커밋은 `8cc4f1b`이며 internal.13 변경은 그 이후 작업이다.
- 최신 변경·검증 결과: `NATURAL_DIAGRAM_EXTRACTION_REPORT.md`.
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
- 소스·배포·문서 커밋 `8cc4f1b`를 `origin/main`에 일반 푸시 완료했다.
  GitHub의 권장 파일 크기 경고는 있었으나 ZIP 포함 푸시는 성공했다.
- 다음: 사내 환경에서 `test-natural-diagram.cmd`와 기존 실패 입력을 시험하고 실제 모델의
  의미 품질·소요 시간을 확인한다. PostgreSQL 실연결 검증도 별도 수행하며 결과는
  `NATURAL_DIAGRAM_RECOVERY_REPORT.md`에 추가한다. 현재 추가 로컬 수정·검증 작업은 없다.

internal.12 구현·로컬 검증·패키징은 완료됐다. 다음 작업은 사내 환경이 제공되면 실제 LLM과
PostgreSQL을 검증하고 결과를 이 파일과 최신 보고서에 추가한다. 새 기능을 시작할 때는 기존
checkpoint와 사용자 변경을 보존하며, 의미 있는 구현·검증·중단 뒤 이 파일의 현재 상태를 갱신한다.

### 2026-09-21 재개 상태 확인

- 구현 계획·최신 복구 계획·진행 기록과 실제 작업 트리를 대조했다. 소스 미커밋 변경은 없으며,
  기존 문서 2개의 완료 기록 변경을 보존했다. HEAD와 로컬 원격 추적 참조 `origin/main`은
  모두 `8cc4f1b`다. 이번 확인에서 원격 서버를 새로 조회하지는 않았다.
- internal.12 ZIP의 SHA-256을 다시 계산하여 배포 체크섬과 일치함을 확인했다.
  보존된 전체 검증 로그와 패키지 검사 9개의 exit 0, 자연어 합성 회귀의 passed를 확인했다.
  이번 세션에서 전체 verify나 실제 LLM·PostgreSQL 검증을 새로 실행한 것은 아니다.
- 다음: 사내 실행 환경 또는 기존 실패 입력·진단 결과 위치를 전달받아 실제 모델 검증을
  진행한다. 다른 중단 작업을 의미한다면 해당 기능·오류를 확인한 뒤 범위를 확정한다.
  현재 기록상 미해결 로컬 구현 실패는 없고, 사내 검증에 필요한 환경 정보는 미제공이다.

### 2026-09-21 internal.13 구현 시작

- 승인 계획: `NATURAL_DIAGRAM_EXTRACTION_PLAN.md`. 사내 고정 검사에서도 추출 검증/의미 거부가
  발생함을 사용자가 확인했다. internal.12 로컬 통과를 실제 모델 해결로 간주하지 않는다.
- 시작 HEAD `8cc4f1b`; 기존 진행 기록/복구 보고서 변경과 internal.12 배포를 보존한다.
- 확인: assumption 후보 고정 후 동일 ID 보정 무시, 검토 형식 보정/의미 판정의 횟수 공유,
  자유 검토 사유가 NaturalSemanticIssue로 합쳐지는 진단 한계.
- 다음: 실패 회귀와 추출 전용 계약/고정 검토 사유/후보별 보정 상태 구현 → 전체 검증 → 패키지.
- 현행 코드에서 신규 회귀 2개가 실제로 실패함을 확인했다: 동일 ID origin 보정 무시,
  검토 형식 오류 2회 뒤 의미 거부 시 다음 후보 검토 차단. 수정 후 관련 .NET 101개 통과.
- 원문 추출 DTO, 근거가 있는 6종 의미 오류, 후보별 검토 예산, 무변화 검토 재사용,
  교체/보존 수치와 사내 복사용 3줄 요약을 구현했다. 원문/응답은 진단에 포함하지 않는다.
- 비동기화 검증 사본 `%TEMP%/DiagramMaker-natural-internal13`을 사용한다. esbuild 상위
  폴더 읽기 제한은 확장 권한 오프라인 빌드로 해결했다. 실제 HTTP/API/화면 회귀 진행 중.

### 2026-09-22 internal.13 검증 재개

- 계획과 현재 변경을 대조했다. HEAD `8cc4f1b`, 기존 checkpoint와 internal.12 배포 및
  모든 미커밋 변경을 보존한다. internal.13 추출·검토 개선의 검증과 패키징을 이어간다.
- 이전 전체 검증 로그는 공유 의미 60,000자 UI 검사 도중 종료되어 최종 성공 기록이 없다.
  비동기화 검증 사본과 원본을 해시로 대조한 뒤 전체 verify를 다시 실행한다.
- 다음: 전체 검증의 최종 종료 코드 확인, 실패 수정, internal.13 ZIP 및 패키지 검사,
  검증 보고서와 재개 기록 마감. 실제 사내 LLM·PostgreSQL 검증은 별도 확인 대기다.
- 원본과 검증 사본의 실행/빌드/테스트 파일 297개를 대조하여 미반영된 마지막 수정 5개를
  검증 사본에 반영했다. 재개 검증에서 .NET 399, 작업자 43, 웹 62, 정책 13 및
  시작 정책 11개가 통과했고 프런트엔드 빌드도 성공했다. 전체 검증의 브라우저 검사는 진행 중이다.
- 공유 의미 기본/60,000자, SVG 6, Sequence 4, 코드·디자인 UI가 통과했다.
  자연어 회귀 `natural-reliability-4ehEld`는 20개 검사·144회 합성 HTTP 요청·20.578초로 통과했다.
  실패 사례별 요약 분리와 NaturalRepairNoProgress, 390/1440px 요약·복사·다운로드를 확인했다.
  다음은 전체 verify의 Windows 실행기 검사 완료 확인 후 internal.13 패키징이다.
- 전체 `verify.ps1` 최종 exit 0. Windows UI 실행기 14개 검사도 통과했다
  (`ui-test-launchers-or8xq5`). 로그는 검증 사본의 `artifacts/internal13-full-verify-20260922.log`.
  다음: internal.13 패키지 빌드, 원본/검증 사본과 ZIP/stage 해시 대조, 패키지 검사 9개.
- internal.13 빌드 exit 0. 원본/검증 사본의 소스·빌드 설정 304개 해시 일치,
  ZIP 96,508,426바이트·1,832파일의 stage 대비 SHA-256 대조 통과.
  ZIP SHA-256 `9778ffbeefec6f390fa56e62831a980ecc615a80edec153ca4cc8173e2790edd`.
  manifest는 기준 커밋 `8cc4f1b` 및 dirty 상태를 반영한다. 검증 보조 스크립트의 ZIP 어셈블리
  로딩 오류는 수정 후 통과했으며 제품 빌드 실패가 아니다. 패키지 검사 9개를 진행 중이다.
- 패키지 검사 7개 exit 0: API/미리보기, 코드 UI, 디자인 UI, 자연어 API/UI,
  합성 LLM 제한/기본 모드, 공유 의미 기본 검사. 자연어 `natural-reliability-SeeE7O`는
  20개 검사·144회 합성 요청·24.876초이며 보고서와 화면의 internal.13 버전도 확인했다.
  다음: 공유 의미 60,000자 및 Windows CMD 검사 종료 확인, ZIP·증적을 원본 artifacts로 보존,
  최종 보고서·진행 기록 마감. 커밋/푸시는 아직 수행하지 않았다.
- 패키지 검사 9개 명령 모두 exit 0. 공유 의미 60,000자 `shared-semantics-0DCAl1`,
  Windows CMD 8개 `windows-launchers-WsLvVn`까지 통과했다. ZIP/SHA를 원본
  `artifacts/release/`에 복사하고 해시를 확인했다. internal.12 ZIP도 원래 해시 그대로 보존했다.
- 전체 검증 로그, 패키지 빌드/검사 로그와 종료 코드, 304개 소스 파일 해시, ZIP 무결성 기록,
  자연어 소스/패키지 회귀 결과와 대표 화면은 `artifacts/internal13-validation/`에 보존했다.
- internal.13 로컬 구현·전체 검증·패키징 완료. 미해결 로컬 실패 없음. 기존 변경과 checkpoint를
  보존했으며 커밋/푸시는 수행하지 않았다. 다음은 사내에서 `test-natural-diagram.cmd` 및 내장 두 사례,
  기존 실패 입력의 캐시 없는 신규 생성 3회를 실행해 의미 품질·조건 보존·소요 시간을 확인하고
  검사 요약을 `NATURAL_DIAGRAM_EXTRACTION_REPORT.md`에 추가하는 것이다.
  실제 사내 LLM·PostgreSQL 실연결·호스트 egress·외부 취약점 피드는 미검증으로 유지한다.

### 2026-09-22 이전 배포 정리 및 원격 반영

- 사용자 요청에 따라 internal.11/internal.12 ZIP과 각 SHA-256 파일을 현재 배포 폴더에서 제거했다.
  이전 커밋과 checkpoint는 보존한다. internal.13 ZIP/SHA만 남았으며 SHA-256이 검증값과 일치한다.
- 소스·회귀 테스트·문서·internal.13 배포본·이전 ZIP 삭제를 함께 커밋하고 `origin/main`에
  일반 푸시한다. 전체 verify와 패키지 검사 9개의 통과 결과는 유지되며 실행 코드 추가 변경은 없다.
- 다음: 원격 브랜치 상태 확인, 커밋/푸시, 원격 커밋 일치 및 깨끗한 작업 트리 확인.
