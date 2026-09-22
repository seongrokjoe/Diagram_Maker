# 시나리오별 자연어 생성 검증 (internal.15)

## 변경 내용

- 자연어 요구사항을 시나리오 단위로 생성·검토한다. 원문 근거와 서버 발급 ID를 유지하고, 형식 간 검토에서 지적한 페이지를 대상으로 보정한다.
- 응답 형식과 내용의 보정 예산을 단위별로 공유하며, 완료 후보·진행 상태·소진 횟수를 재개 시 보존한다. 코드 블럭과 Git 의미 검토도 구체적 지시와 근거 ID를 사용한다.
- 관리자용 자연어 고정 검사는 문장·형식 선택, NDJSON 진행 표시, 취소, 남은 시간, 완료 페이지와 원인별 요약을 제공한다. 실제 사용자 원문이나 모델 응답은 보고서에 포함하지 않는다.
- 새 생성 계약은 `natural-v11`, `natural-design-v7`이다. 기존 결과와 수동 편집은 유지된다.

## 로컬 검증

- 비동기화 복사본에서 `powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1` exit 0: .NET 428, Node 작업자 43, 웹 64, 정책 13 및 Windows UI 실행기 14개 통과.
- API 기본·간편 모드, 공유 의미 기본·60,000자, SVG 6개, Sequence 4개, 코드·디자인 UI 및 자연어 합성 API/UI 검사 통과. 자연어 검사에는 선택된 State 형식의 NDJSON 진행·완료와 잘못된 선택의 거부를 포함한다.
- 사내 실제 LLM, PostgreSQL 연결, 호스트 egress 및 실제 모델 응답 시간은 이 환경에서 측정하지 않았다. 사내 고정 검사와 기존 실패 입력을 새 생성으로 다시 확인해야 한다.

## 배포

- Windows 패키지: `artifacts/release/DiagramMaker-0.1.0-internal.15-win-x64.zip` 및 `.sha256`.
  ZIP은 96,554,667바이트·1,832파일이며 stage 파일과 SHA-256이 모두 일치한다.
- SHA-256: `4021e89e1dcd9ce4ce3bbb631cc9eb065270a2f3208beef8c1edc9fbcbd6de46`.
- 패키지 검사 9개 명령 최종 exit 0: 미리보기, 코드·디자인 UI, 자연어 합성 API/UI,
  LLM 전송 기본·제한 모드, 공유 의미 기본·60,000자, Windows CMD 8개. 첫 60,000자
  화면 검사에서 리비전 저장 버튼 대기 시간 초과가 1회 발생했고 단독 재실행에서 통과했다.
  첫 실패와 최종 결과를 `artifacts/internal15-validation/`에 보존했다.
- 이전 internal.13/internal.14 ZIP과 SHA 파일 4개는 새 패키지 검증 후 배포 폴더에서 제거했다.
