# AI/Code 다이어그램 개선 검증 기록

2026-09-14. 승인 범위: `AI_CODE_DIAGRAM_IMPROVEMENT_PLAN.md`.
기존 internal.7 체크포인트와 사용자 변경을 보존했다.

## 구현 내용

- C++ 선언 초기화와 클래스/전역 동명 호출 해석을 보완했다.
- 호출 이름·인수·반환 대상·출력 인자와 근거를 보존한다. 코드 근거와 동봉 Win32 API 계약을 구분한다.
- 모든 AI 설명의 의미 검토를 유지하며 공유 요청을 줄이고, 정상 비교식을 허용한다.
- AI/Code 생성 원본과 편집 이력을 독립 보존하며 원본별 조회·편집·집계와 기존 저장 자료 호환을 제공한다.
- 요청 오류를 개별 행으로 보존하고 복구 상태와 선택 오류의 상세 내용을 표시한다.
- 선택 실행 내 이름 검색, AI/Code 결과 트리·배지, 대응 페이지 함께 보기를 제공한다.
- 브라우저 보기 방향, 원본 기준 100–1600% 확대, 별도 맞춤 보기를 제공한다.
- 구조 편집에서 내부 ID를 숨기고 노드·관계 목록을 펼쳐서 편집한다.

## 재개 세션에서 해결한 문제

- 접힌 관계 목록에 수만 개의 노드 선택 항목을 미리 생성하던 동작을 제거했다. 목록을 펼칠 때만 입력 요소를 생성한다.
- 큰 그림 편집 검사는 변경된 이름이 SVG에 반영된 다음 저장하며, 저장 결과의 Code 원본 ID와 노드 값을 확인한다.
- Git 비교 화면에서 도구 버튼이 좁게 눌리지 않도록 줄바꿈을 적용했다.
- 폭이 작은 Git 그림에서도 변경 범례 전체가 SVG 영역에 포함되도록 보정했다.
- 모바일 오류 표는 표 내부에서 가로로 스크롤하며 단계명·시각·복구 상태를 한 글자씩 줄바꿈하지 않는다.

## 검증

마지막 모바일 CSS/검사까지 포함한 전체 `verify.ps1`이 **실제 종료 코드 0**으로 완료됐다.
검증 시간은 2026-09-14 21:06:53–21:14:56 KST다. 미해결 로컬 빌드·회귀 실패는 없다.

| 검사 | 결과 |
| --- | --- |
| .NET / C++ 작업자 / 웹 단위 | 302 / 43 / 50개 통과 |
| 정책·폰트·실행기 단위 | 13개 통과 |
| 프런트엔드 빌드·라이선스·배포 소스 제한 | 통과, SBOM 293개 구성 요소 |
| 시작 정책 / API | 11개 / 기본·일반 2모드 통과 |
| 공유 의미·AI/Code 원본·편집·화면 | 기본·60,000자 한도 모두 통과 |
| SVG 보안·색상 | 6개 통과 |
| 코드 블럭·자연어·Git UI | 통과 |
| 실제 Windows CMD | 14개 통과 |

- 명령: 비동기화 검증 작업본에서 `powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1`.
- 실행 도우미: `artifacts/ai-code-resume/finish-verification.ps1` → `artifacts/ai-code/run-checks.ps1 -Mode verify`.
- 전체 로그: `artifacts/ai-code-resume/verify-completion.log` 및 `verify-completion.stderr.log`.
- 종료 결과: `artifacts/ai-code-resume/verify-completion-result.json`.
- 검증 입력: `artifacts/ai-code-resume/completion-verify-inputs.json`의 266개 파일이 현재 소스와 일치한다.
- 기본 권한에서 발생한 esbuild 상위 경로 접근 오류는 동일 오프라인 명령의 확장 권한 실행으로 해소했다.
- 최종 [증적 요약](artifacts/ai-code-resume/evidence-summary.json)에 종료 코드·소스 일치·8개 픽스처를 기록했다.
  [소스 해시](artifacts/ai-code-resume/source-hashes.json), 캡처 57개, CMD 로그를 함께 보존했다.
  `git diff --check`도 통과했다.

직접 확인한 대표 화면:

- [AI/Code 데스크톱 비교](artifacts/ai-code-resume/shared-semantics-nt0iEr/ai-code-compare-1440.png)
  · [모바일 비교](artifacts/ai-code-resume/shared-semantics-nt0iEr/ai-code-compare-390.png)
- [Git 비교와 변경 범례](artifacts/ai-code-resume/shared-semantics-nt0iEr/git-ai-code-compare.png)
- [오류 표 데스크톱](artifacts/ai-code-resume/code-block-ui-GTvWgf/semantic-progress-1440.png)
  · [모바일 오류 표](artifacts/ai-code-resume/code-block-ui-GTvWgf/semantic-progress-390.png)

합성 1,212행/40함수의 공유 의미 검사에서는 다음 결과를 확인했다. 생성 요청과 의미 검토 요청을 모두 포함한다.

| 입력/형식 | 요청 수 | 보존 페이지 |
| --- | ---: | ---: |
| 코드 Flow | 8 | 41 |
| 코드 3종 | 14 | 43 |
| Git 3종 | 28 | 51 |

실제 사내 LLM 의미 품질·처리 시간과 PostgreSQL 실연결은 이 합성 검증에 포함되지 않는다.
외부 취약점 피드와 호스트 통신 캡처도 실행하지 않았다. 현재 소스 UI는 `start-ui-test.cmd`로 확인할 수 있으며,
이 실행기는 LLM을 끈다. 실제 모델 검사는 승인된 사내 환경에서 수행한다.
이 재개 세션에서는 새 배포 ZIP 생성이나 커밋/푸시를 수행하지 않았다.
