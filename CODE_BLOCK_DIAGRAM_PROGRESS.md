# 코드 블럭·자연어 다이어그램 진행 기록

다음 작업은 `AGENTS.md`, `CODE_BLOCK_DIAGRAM_IMPLEMENTATION_PLAN.txt`, 이 파일을 먼저 읽는다.
전체 제품 설명과 사용법은 `README.md`, 보안 기준은 `SECURITY.md`를 따른다.

## 현재 배포 상태

- 최신 사내 시험 배포: `0.1.0-internal.11`.
- 배포 파일: `artifacts/release/DiagramMaker-0.1.0-internal.11-win-x64.zip` 및 SHA-256 파일.
- ZIP 크기 96,472,718바이트, 1,831파일.
- SHA-256: `ba9e58c272e64d32a48b279b052083dda17e094f336ce462d99ae0ab227ed585`.
- 기준 커밋은 `c0a545ec442cf0c7b64eeacb1230df97d2939f07`이며 internal.11 변경은 그 이후 작업이다.
- 코드 블럭 기능 구현 전 checkpoint `e53efc2`와 기존 사용자 변경은 Git 이력에 보존되어 있다.

## 마지막 작업: 자연어 다이어그램 안정성 개선

승인 범위와 최종 동작은 `NATURAL_DIAGRAM_RELIABILITY_PLAN.md`, 검증 결과와 한계는
`NATURAL_DIAGRAM_RELIABILITY_REPORT.md`에 기록했다.

- 서버 발급 원문 근거 ID와 UTF-16 범위, 복수 근거, 마스킹 전후 위치 연결.
- 요구사항 추출·검토, 공유 시나리오 계획, 형식별 설계·보정, 형식 간 최종 검토.
- 최초 생성 뒤 최대 두 차례 보정, 정상 항목 보존, 출력 한도 시 상세 페이지 분할.
- 최대 5개 질문의 저장·답변·재시작 복원과 revision·질문 버전·소유자 ACL 검증.
- 실행 체크포인트·진단 다운로드, 선택 상세 재생성, 폴링 장애 복구.
- InMemory/LocalFile/PostgreSQL 계약의 lease·취소·질문 대기·재개 일치.
- 새 생성 버전 `natural-v7`, 설계 계약 `natural-design-v3`.

## 최종 검증

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

internal.11 구현·로컬 검증·패키징은 완료됐다. 다음 작업은 사내 환경이 제공되면 실제 LLM과
PostgreSQL을 검증하고 결과를 이 파일과 최신 보고서에 추가한다. 새 기능을 시작할 때는 기존
checkpoint와 사용자 변경을 보존하며, 의미 있는 구현·검증·중단 뒤 이 파일의 현재 상태를 갱신한다.
