# 코드 블럭 다이어그램 구현 진행 기록

다음 세션은 이 파일과 `CODE_BLOCK_DIAGRAM_IMPLEMENTATION_PLAN.txt`, `AGENTS.md`를 먼저 읽는다.
사용자는 전체 계획 구현을 승인했으며 구현 전에 기존 작업 전체를 커밋/푸시하도록 지시했다.
허가된 작업을 이어서 수행하고, 기존 변경을 삭제하거나 덮어쓰지 않는다.

## 현재 상태

- 기준 브랜치: `main`, 원격: `origin` (기존 GitHub 저장소).
- 구현 전 HEAD: `58d703f` (Promote offline package to version 15).
- 기존 작업: 의미 기반 Git 다이어그램/설명/근거 UI, 격리 Codex 합성 샘플 테스트,
  offline.16 패키지 및 관련 테스트/스크립트. 아직 checkpoint 커밋/푸시 전.
- 현재 단계: **P0 — 기존 작업 검증 및 원격 checkpoint**.
- 새 기능 코드 구현은 아직 시작하지 않았다.

## 단계별 체크리스트

- [ ] P0 기존 변경 검토, 전체 검증, checkpoint commit/push 확인
- [ ] P1 계약/설정/3종 저장소/소유자 검증/API/취소/lease 및 테스트
- [ ] P2 C#/C++ 코드 조각 분석/원본 위치/호출 후보/상태 전이 및 테스트
- [ ] P3 그룹 제안/질문/사용자 관계/답변 무효화 및 테스트
- [ ] P4 의미 이해/추천/5종 생성/근거/상세 페이지/재생성 및 테스트
- [ ] P5 새 탭/그룹/질문/결과/편집/이력 화면 및 UI 검증
- [ ] P6 전체 verify, 문서/패키지 확인, 실제 내부 LLM 품질 검증 상태 기록

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

1. `git status --short`, 이 파일, baseline verify 로그 마지막 부분을 확인한다.
2. P0가 미완료라면 검증 결과/기존 변경의 비밀값/패키지 구성을 확인한 후 기존 작업과
   계획/진행 문서를 커밋하고 origin/main에 일반 push한다. 강제 push 금지.
3. 원격 HEAD 일치를 확인한 후 P1부터 계획을 구현한다.
4. 각 단계의 실제 파일/검증 명령/결과/남은 작업을 아래 변경 이력에 기록한다.

## 변경 이력

- 2026-09-08: 실행 승인 수신. 계획/진행 기록 생성. baseline 검증 시작.
- 2026-09-08: baseline 검증 완료(exit 0). checkpoint 커밋/원격 push 준비 완료.
