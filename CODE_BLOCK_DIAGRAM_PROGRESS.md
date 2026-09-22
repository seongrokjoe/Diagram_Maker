# 코드 블럭·자연어 다이어그램 진행 기록

## 2026-09-22 internal.14 공통 안정화 시작

- 승인 계획: `SHARED_DIAGRAM_RELIABILITY_PLAN.md`. 시작 HEAD `65c381f`, 작업 트리 깨끗함.
- 자연어 설계 제안 유지, 검증된 부분 결과 보존. 자연어 실제 오류는 `NaturalRequirementCoverageMissing`.
  코드 블록은 보정 소진 문구만 확인되어 원인을 동일하다고 단정하지 않는다.
- 추가 승인: 횟수 제한으로 실패하는 경로의 자동 보정은 최초 요청 이후 최대 10회로 변경한다.
- 계획 수립 기준선: 자연어/공통 검토/실행 관련 .NET 85개 통과. 실제 사내 모델 검증은 미수행.
- 다음: 공통 복구 정책/후보별 검토/연속 내용 실패 격리 회귀와 자연어 의미 계획 계약 구현.
- 1차 구현: 공통 보정 한도 최초+10회, 응답별 체크포인트, 코드/Git 연속 내용 실패 시 뒤 묶음 건너뛰기 제거,
  자연어 요구사항별 의미 계약과 서버 ID/근거 조립 및 통합 검토를 추가했다. 새 버전은 natural-v10,
  natural-design-v6, shared-semantic-v7/shared-requests-v9이다.
- API 빌드 통과, 자연어 설계 관련 15개 통과. 전체 .NET 기준선은 373 통과/26 실패.
  기존 보정 횟수·응답 계약을 가정한 회귀를 갱신 중이며 저장소 경로 테스트 1개는 OneDrive 환경 영향이다.
  완료로 간주하지 않는다. 다음: 10회 경계/실행 격리/선택 재생성 회귀, 자연어 통합 연결, UI·사내 검사.

### 2026-09-22 internal.14 재개

- 중단된 변경을 보존하고 `%TEMP%/DiagramMaker-natural-internal14`에 비동기화 검증 복사본을 만들었다.
  최초 재개 .NET 검증은 397 통과/2 실패. 두 실패는 폐기된 연속 3회 중단 정책을 가정한 요청 수 검사이며,
  새 단위별 실패 격리 정책과 재개 시 추가 요청 없음 검사를 유지하도록 갱신했다.
- 자연어 통합 시 기존 단위 내부 구조 변경을 거부하고, 독립 요구사항의 Sequence 구분을 실제 출력에 연결했다.
  서버 ID/근거 조립, 개별 멤버의 설계 가정, 최초+10회 경계와 형식 보정 도중 재개 회귀를 추가했다.
  UI 진단 단계와 합성 HTTP 검사도 새 의미 계약에 맞춰 갱신 중이다.
- 추가 테스트의 컴파일 오류 2개(일반 클래스에 with 사용, xUnit 분석 규칙)를 수정했다.
  다음: 새 회귀 실행, 실제 loopback/API/UI 검증 및 전체 verify, internal.14 패키지와 문서 마감.
  사내 실제 모델과 PostgreSQL 연결 정보는 미제공이며 해당 검증은 아직 수행하지 않았다.
- .NET 410개 통과, 프런트엔드 빌드 성공. esbuild 상위 폴더 읽기 제한은 승인된 확장 권한으로 해결했다.
  자연어 loopback/API/Edge 회귀 `natural-reliability-LJIhxD`가 통과했다.
  요구사항 추출의 무진행 후보는 최초+1회 뒤 중단하도록 보완했으며 코드/Git 개별 계획 경로에도
  후보 검토 재사용, 별도 검토 형식 보정과 요청별 시도 식별을 반영했다. 다음: 전체 verify와 패키징.
- 전체 verify 재실행에서 .NET 410, 작업자 43, 웹 62, 정책 13, 시작 정책 11 및 API 두 모드가 통과했다.
  공유 합성 서버가 자연어 의미 요청의 context를 구형 코드 요청 포장으로 오인해 클래스 계약을 깨뜨렸다.
  합성 서버에서 새 요청 계약을 구별하도록 수정했다. 다음: 공유 검증 재실행과 전체 verify 마감.

### 2026-09-22 internal.14 최종 검증 재개

- HEAD `65c381f`와 기존 미커밋 변경 및 checkpoint를 보존했다. 이전 전체 verify는 공유 합성 서버의
  구형 계약 처리에서 실패했고, 후속 검사는 화면 밖으로 끝나는 드래그에서 실패한 기록을 확인했다.
- 드래그 검사 좌표가 전체 이동 중 화면 안에 머물도록 보완하고 실패 시 전후 스크롤 위치를 남긴다.
  비동기화 검증 복사본에 최신 소스를 반영했다. 다음: 공유 검사 결과 확인 → 전체 verify →
  internal.14 패키지 생성·검증·원본 artifacts 보존 → 보고서 마감.
- 이전 패키지 빌드는 publish 이후 중단되어 ZIP이 생성되지 않았다. 실제 사내 LLM·PostgreSQL은
  여전히 연결 정보 미제공으로 미검증이며 합성 검사와 구분한다.
- 공유 합성/API/Edge 검사 `shared-semantics-gJPTqn` exit 0. 자연어 네 형식의 직접 편집·빈 공간/
  Space 드래그·리비전 복원과 코드/Git 정상·부분 결과 보존을 확인했다. 전체 verify를 재실행한다.
- 재개 전체 verify에서 .NET 410, 작업자 43, 웹 62, 정책 13, 시작 정책 11개가 통과했고
  프런트엔드 빌드와 라이선스 검증이 성공했다. API/브라우저/실행기 검증 종료를 기다린다.
- internal.14 패키지 빌드 exit 0. 소스·빌드·검사 파일 312개의 원본/복사본 SHA-256이 일치하며
  ZIP 96,528,255바이트·1,832파일이 stage와 일치한다. SHA-256은
  `78bc146f54f121269195aeb7a891dcb1593e52b07714fb6febf540bf9a57c2db`이다.
  manifest는 기준 `65c381f`와 dirty 상태를 기록한다. 다음: 전체 verify 종료와 패키지 검사 9개 확인.
- 전체 verify의 공유 의미 기본/60,000자 검사(`shared-semantics-BIK9uI`/`shared-semantics-Jq5XFG`),
  SVG 안전성 6개와 Sequence 배치 4개가 통과했다. 패키지 API/미리보기·코드 UI·디자인 UI도 통과했다.
- 자연어 회귀 20개가 소스 `natural-reliability-mA9qFF`와 패키지 `natural-reliability-cAyjh6`에서
  각각 406회 합성 요청으로 통과했다. 소스 32.657초, 패키지 37.533초이며 실제 모델 성능 측정이 아니다.
  패키지 검사 7개가 exit 0이며, 공유 60,000자와 Windows CMD 검사가 남았다.
- 패키지 공유 60,000자 검사에서 코드 리비전 저장 버튼의 Playwright click 10초 시간 초과가 발생했다.
  앞선 동일 소스 검사는 통과했다. 실패 로그와 첫 시도 결과를 보존하며 전체 verify의 실행기 빌드가
  끝난 뒤 해당 검사만 단독 재실행하여 재현 여부를 확인한다. 현재 패키지 검증 완료로 간주하지 않는다.
- 전체 `verify.ps1` 최종 exit 0. Windows UI 실행기 14개(`ui-test-launchers-SMIKm5`)까지 통과했다.
  이제 패키지의 실패한 60,000자 검사와 마지막 Windows CMD 검사만 단독 실행한다.
- 재개 보조 스크립트가 PowerShell의 JSON 배열 파이프 처리를 잘못 가정해 앞선 패키지 검사를
  다시 시작했다. 중복 실행을 중단하고 배열을 먼저 변수로 받아 통과한 7개를 보존하도록 수정했다.
  제품 소스/패키지는 변경하지 않았으며 첫 시도 결과 JSON과 실패 로그는 유지한다.
- 패키지 공유 60,000자 검사는 제품/시간 제한 변경 없이 단독 재실행에서 통과했다.
  마지막 Windows CMD 검사는 구형 `natural-design-v5` 기대값 때문에 실패했다. 실제 v6에 맞게
  검사만 갱신했으며 통과한 8개를 보존하고 CMD 검사만 다시 실행한다. ZIP 내용은 동일하다.
- Windows CMD 8개(`windows-launchers-acbynb`)가 통과했고 패키지 검사 9개 명령 모두 최종 exit 0이다.
  최신 원본/복사본 소스 312개와 `git diff --check`를 확인했다. ZIP/SHA를 원본 `artifacts/release/`에
  복사하고 해시를 확인했다. internal.13 ZIP과 checkpoint는 보존했다.
- 전체 verify·패키지 빌드·검사 로그, 첫 실패 및 최종 결과 JSON, 파일 해시와 화면 증적은
  `artifacts/internal14-validation/`에 보존했다. 보고서는 `SHARED_DIAGRAM_RELIABILITY_REPORT.md`이다.
- internal.14 로컬 구현·전체 검증·패키징 완료. 미해결 로컬 실패 없음. 커밋·푸시는 수행하지 않았다.
  다음: 사내 환경에서 `test-natural-diagram.cmd`, 코드 검사와 기존 실패 입력을 신규 생성으로 검증하고
  실제 의미 품질·조건 보존·응답 시간을 기록한다. 사내 실제 LLM/PostgreSQL 연결 정보 미제공이며
  실제 모델·DB·호스트 egress·외부 취약점 피드 검증은 미수행으로 남긴다.

다음 작업은 `AGENTS.md`, `CODE_BLOCK_DIAGRAM_IMPLEMENTATION_PLAN.txt`, 이 파일을 먼저 읽는다.
전체 제품 설명과 사용법은 `README.md`, 보안 기준은 `SECURITY.md`를 따른다.

## 현재 배포 상태

- 최신 사내 시험 패키지: `0.1.0-internal.14`. 이전 internal.13 ZIP/SHA도 보존했다.
- 배포 파일: `artifacts/release/DiagramMaker-0.1.0-internal.14-win-x64.zip` 및 SHA-256 파일.
- ZIP 크기 96,528,255바이트, 1,832파일.
- SHA-256: `78bc146f54f121269195aeb7a891dcb1593e52b07714fb6febf540bf9a57c2db`.
- 기준 커밋은 `65c381f`이며 internal.14 구현은 미커밋 작업이다.
- 최신 변경·검증 결과: `SHARED_DIAGRAM_RELIABILITY_REPORT.md`.
- 이전 internal.13 구현·배포 커밋 `26e2bfb`와 전달 기록 `65c381f`는 보존했다.
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
- 구현·회귀·문서·internal.13 ZIP/SHA·이전 배포 정리 커밋 `26e2bfb`의 `origin/main`
  일반 푸시가 성공했다. GitHub는 ZIP 크기 권장 한도 경고를 표시했으나 푸시를 수락했다.
  현재 배포에는 internal.13만 있으며 이전 버전과 checkpoint는 Git 이력에 보존되어 있다.
  완료 기록을 문서 커밋으로 반영한 뒤 원격 HEAD 일치와 작업 트리를 최종 확인한다.
  이후 남은 작업은 사내 실제 모델 및 PostgreSQL 검증이다.
