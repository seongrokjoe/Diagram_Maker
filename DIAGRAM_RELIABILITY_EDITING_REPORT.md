# 생성 안정성·직접 편집 개선 검증 기록

기준: `e224ae42d00c63b13dcc4a58541416dff5af20aa`, 승인 계획: `DIAGRAM_RELIABILITY_EDITING_PLAN.md`.
기존 체크포인트와 사용자 변경을 보존하며 internal.10 구현·검증·패키지 전달을 완료했다. Git push는 수행하지 않았다.

## 구현 동작

- 자연어·코드·Git·저장소·LLM 점검의 오류를 기능별로 분리하고 오래된 비동기 결과의 화면 덮어쓰기를 방지한다.
- 자연어 요구사항을 원문 UTF-16 범위와 연결하고, 문단별 시나리오를 선택한 형식 안의 여러 페이지로 생성한다.
  실행 상태·진행·완료 페이지를 저장하고 취소·이어하기·선택 형식/시나리오 재생성을 제공한다.
- 코드/Git 의미 검토에서 통과한 설명은 부분 AI로 보존한다. 실패 항목만 한 번 보정하고,
  실패 사유·필드·근거·보정 지시를 페이지 설명에 남긴다. Code 원본은 별도로 유지한다.
- 클래스 멤버·시퀀스 조건/메모까지 SVG 직접 편집을 확장한다. 선택 시 SVG를 교체하지 않아 더블클릭이 유지되며,
  클래스 멤버의 화면 순서와 원본 인덱스를 연결한다. undo/redo·저장·복원과 수동 편집 출처를 보존한다.
  저장된 편집 이력을 불러오는 동안 새 편집 진입을 차단하여 늦게 도착한 이력이 편집 초안을 덮어쓰지 않도록 한다.
- 보기 드래그, 편집 빈 공간/Space 드래그, 기존 휠/확대를 함께 지원한다.
- Git 변경점 재선택 시 그룹 설정·순서를 복원하고, 새 그룹 만들기와 바로 배정·출력 삭제를 명확히 표시한다.

## 검증

최종 전체 `scripts/verify.ps1 -UseInstalledDependencies`는 **exit 0**으로 완료했다.
검증 시각: 2026-09-17 07:25:15–07:34:04 KST. 패키지 검증은 아래 배포 절에 별도로 기록한다.

- .NET 348, Node 작업자 43, 웹 62, 정책 단위 13, 시작 정책 11개 통과.
- API 두 모드, 공유 의미 기본/60,000자 두 조건, SVG 6개, Sequence 배치 4개 통과.
- 코드 UI `code-block-ui-XIt06s`, 디자인 UI `design-ui-YkMeFs`, Windows CMD 14개
  `ui-test-launchers-nFcm4E` 통과.
- 두 공유 의미 검증(`shared-semantics-yKo1xj`, `shared-semantics-8LZYys`) 모두 1,212행/40함수에서
  코드 단일 형식 8회·41페이지, 코드 3종 10회·43페이지, Git 3종 20회·51페이지를 보존했다.
- 검증 시 소스 293개 SHA-256이 검증 작업본과 일치했다. 종료 JSON·소스 해시·10개 픽스처의 증적 137개는
  `artifacts/reliability-editing/`에 보존했다.

- 초기 회귀: .NET 348, Node 작업자 43, 웹 62, 정책 13개 통과.
- 추가 통합 `shared-semantics-qSOcEX`: Git 5종, 실제 API의 부분 AI 2페이지와 정적 원본 분리,
  저장/재조회·실패 상세·1440/390px 화면 통과.
- 동일 통합: 자연어 4종, 백그라운드 실행, 선택 시나리오 재생성, 실패 시 이전 결과 보존,
  클래스 멤버·시퀀스 조건 편집, undo/redo, 팬 3종, 새로고침 후 리비전 복원 통과.
- 첫 전체 검증은 esbuild 상위 폴더 읽기에 대한 샌드박스 제한으로 중단되어 확장 권한으로 재검증했다.
  신규 부분 AI UI 검사의 숨겨진 프리셋 SVG 선택 오류는 실제 편집기 범위로 수정했다.
- 후속 자연어 통합 `shared-semantics-qS1TCp` 통과: 지연된 편집 이력 응답 중 편집 차단,
  직접 편집·화면 이동·리비전 복원과 Git 부분 AI API/UI 회귀를 확인했다.
  390px 부분 AI 실패 상세 화면과 클래스 멤버 편집 결과 캡처를 직접 검토했다.

## 저장·운영 영향

자연어 실행의 `natural_diagram_runs` PostgreSQL 테이블과 LocalFile 상태 저장을 추가한다.
기존 데이터의 선택 필드와 문자열/숫자 실행 상태 호환을 유지한다. 실행은 소유자 ACL과 revision/lease로 보호한다.
원문·요구사항·중간 결과 보존 정책은 `SECURITY.md`에 기록했다. 별도 새 설정은 필요하지 않다.

실제 사내 LLM 의미 품질, 동일 모델 3회 중앙값 처리 시간 30% 단축, PostgreSQL 실연결·동시성은
현재 PC에서 측정하지 않았다. 합성 loopback 검증을 이들 검증의 대체 결과로 주장하지 않는다.

## 배포

internal.10 빌드, 패키지 API·코드/디자인 UI·합성 LLM 통신 두 모드·대형 입력 두 조건·Windows 실행기
9단계가 모두 **exit 0**이다(최종 2026-09-17 07:43:24 KST). 기존 internal.9 ZIP과 체크섬은 보존했다.

[Windows ZIP](artifacts/release/DiagramMaker-0.1.0-internal.10-win-x64.zip)과
[SHA-256 파일](artifacts/release/DiagramMaker-0.1.0-internal.10-win-x64.zip.sha256)을 전달했다.

- ZIP: 1,831개 파일, 96,443,863바이트.
- SHA-256: `77d23b45bfe5ea2a677ac453446d95385cd746e18500c3d48521d45460a5be9d`.
- 모든 ZIP 파일은 stage와 SHA-256 일치. 작업자 소스·실행 가이드 일치,
  실행 데이터·저장소·인증·실제 LLM/네트워크 정책 미포함을 확인했다.
- 패키지의 기준 커밋은 `e224ae42d00c63b13dcc4a58541416dff5af20aa`, `sourceTreeDirty=true`다.
  이번 변경을 포함한 실제 소스는 검증 해시 목록으로 식별한다. 새 커밋·push는 수행하지 않았다.
- 패키지 공유 의미 `shared-semantics-ciS3Kn`/`shared-semantics-C4YSB3`도 8/10/20회·41/43/51페이지 유지.
  코드 UI `code-block-ui-55KfkL`, 디자인 UI `design-ui-FKsSEq`, Windows 실행기 7개 검사 통과.
- 8개 패키지 픽스처에서 증적 129개를 수집했다. 패키지 390px 코드 결과와 부분 AI/시퀀스 캡처를 직접 확인했다.
- 검증 뒤 소스 목록의 변경은 배포 정보 `scripts/ui-test-package.json` 1개뿐이다.
  새 ZIP/웹 자산 해시를 대조했고 실행 소스는 동일하다. 최종 대조는 `delivery-source-audit.json`에 기록했다.

검증 로그·캡처·소스 해시는 `artifacts/reliability-editing/`에 보존한다.
