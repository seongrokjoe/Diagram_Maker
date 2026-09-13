# 코드 다이어그램 서버 호환·오류 복구 안정화

기준 커밋 `883b8cd`, 시험 버전 `0.1.0-perf.4`. 기존 perf.3 ZIP과 체크포인트를 보존한다.

## 변경 동작

- 서버 오류는 HTTP 상태와 고정 분류로 기록한다. 인증·모델·문맥·출력 한도·출력 필드·스키마 제약·템플릿·서버 상태를 구분하며 서버 응답 원문은 보고서에 포함하지 않는다.
- 출력 방식 협상은 요청 스키마와 Thinking 설정별로 수행한다. 미지원 필드 오류에만 `structured_outputs` → `response_format` → JSON 프롬프트로 진행한다. 알 수 없는 HTTP 400을 반복하지 않는다.
- 공유 생성/검토가 스키마 제약 오류를 받으면 길이·배열 수 제약을 줄인 호환 스키마를 한 번 시도한다. 필수 필드·타입·허용 ID·문제 코드와 로컬 검증은 유지한다. 검토 스키마의 `uniqueItems`는 제거하고 중복은 로컬에서 거부한다.
- 최종 시스템/사용자 메시지와 스키마를 기준으로 문자·입력 토큰·출력 예약을 검사한다. 출력 방식 변경 뒤에도 다시 검사하므로 JSON 프롬프트 전환이 입력 한도를 우회하지 않는다.
- 초기 생성은 최대 12항목이며 작은 출력 설정에서는 더 작게 나눈다. 생성 출력 최대 8,000, 검토 기본 2,000토큰을 사용한다. 앱과 정책 예제의 검토 기본값을 통일했고 명시적 설정은 존중한다.
- ID가 유효한 응답의 표현 오류는 해당 항목만 수정한다. 정상 항목의 생성·검토를 보존한다. 수정 요청을 나눌 때 거절된 응답과 피드백 모두 해당 항목만 포함한다.
- 생성 수정 1회, 검토 응답 교정 1회, 최대 분할 깊이 12를 유지한다. 영구 실패 3묶음 연속이면 남은 묶음을 중단한다. 서버 공통 오류는 현재 실행의 후속 LLM 요청을 즉시 멈추고 재개할 체크포인트를 남긴다.
- 실패한 페이지에 생성/응답 검증/의미 검토 단계를 전달한다. 진단 화면에 HTTP 상태·조치·호환 스키마를 표시하고 코드 블럭/Git 진단에 텍스트 다운로드를 추가했다.
- LLM 점검의 **코드 생성 검사**와 `test-code-diagram.cmd`는 고정 C#/C++의 파서·공유 생성·검토·페이지 구성을 Thinking OFF로 실행한다. 설정과 요청 진단을 텍스트로 저장하며 사용자 작업을 만들지 않는다. 기존 Admin 권한을 사용한다.
- 코드 블럭/Git의 텍스트 진단 버튼 문구를 통일하고 다운로드 시작 후 임시 URL을 해제한다. LLM 점검 화면은 작은 창에서 한 열로 배치하며 검사 결과와 보고서 버튼이 화면 밖으로 넘치지 않는다.
- 미사용 `RefineAnalysisDiagramAsync` 계약과 구현을 제거했다. 기존 `PlanDiagramAsync`와 구버전 실행 재개 경로는 유지한다. 공유 요청 정책은 v4로 구분하며 전체 설정 지문으로 체크포인트와 코드 블럭 결과 캐시를 식별한다.

## 검증 및 수정한 회귀

초기 .NET 회귀에서 61/68개가 통과했다. 초기 분할 정책 변경으로 기존 요청 수·1초 재시작 시험이 맞지 않았고, 연속 실패 중단이 누락되어 이를 복구했다. 다음 전체 검사에서는 241/243개가 통과했으며, 기존 큰 묶음용 문자 경계 사례를 12항목에 맞춰 재구성했다. 이 과정에서 분할 자식에 다른 항목의 수정 피드백이 남는 실제 문제를 발견해 수정했다. 해당 예산 회귀 13개는 통과했다.

최종 `verify.ps1`은 exit 0이다. .NET 245개, 작업자 32개, 프런트엔드 46개, 정책 7개,
시작 정책 11개, 빌드·라이선스·API 두 모드·공유 의미 기본/60,000자·SVG 6개·Edge UI가 통과했다.
최종 로그는 `artifacts/perf4-validation/perf4-verify-final-release.log`에 보존한다.
공유 의미 검사는 `shared-semantics-WA8MIx`/`shared-semantics-m2PPv5`, UI는 `code-block-ui-VaIVbl`이다.

중단 지점의 UI 재검사에서 텍스트 진단 버튼 문구 불일치와 LLM 점검 화면의 1080px 최소 폭 상속을 수정했다. 수정 후 `code-block-ui-hFGlDz`의 전체 UI 검사는 exit 0이며 1440px/390px 캡처를 직접 확인했다. 앞선 비정상 프로세스 종료 코드의 OS 원인은 확정하지 않았고, 수정 뒤에는 정상 종료했다.

1,212줄·40함수 합성 사례에서 모든 기존 페이지와 근거를 보존한다. 12항목 분할은 안정성을 위한 변경이며 요청 수는 perf.3보다 증가한다.

| 사례 | 생성 HTTP | 검토 HTTP | 전체 HTTP | 페이지 |
| --- | ---: | ---: | ---: | ---: |
| 코드 블럭 Flow | 7 | 7 | 14 | 41 |
| 코드 블럭 Flow·Class·관계도 | 11 | 11 | 22 | 43 |
| Git 변경 3형식 | 25 | 25 | 50 | 51 |

사내 실제 LLM의 의미 품질·300초 완료와 PostgreSQL 실연결은 이 PC에서 수행하지 않는 기존 결정을 유지한다. 합성 응답 통과를 실제 모델 검증으로 간주하지 않는다.

## Windows 패키지 및 최종 검증

`build-offline-win-x64.ps1 -Version '0.1.0-perf.4' -SkipTests`가 exit 0으로 완료됐다.
최종 .NET 검사는 직전 전체 verify에서 수행했고, 패키징은 내장 x64 Node로 작업자 32개와
프런트엔드 46개를 다시 검사했다. self-contained 게시, 라이선스/SBOM과 외부 추론 제외 검사도 통과했다.

- 파일: `artifacts/release/DiagramMaker-0.1.0-perf.4-win-x64.zip` 및 같은 이름의 `.sha256`.
- 크기: 94,247,091바이트, 1,820개 파일.
- SHA-256: `e837d257cc3f45da5cd3c9552c9458635f8a2cb28ff63089090db4e044bc681b`.
- ZIP 전체 파일과 배포 폴더, 소스 224개와 검증 작업본, 전달 ZIP/체크섬의 복사본이 일치한다.
- `test-code-diagram.cmd`, 한국어 시험 안내와 LLM 정책 예제가 포함되며 원본과 일치한다.
- 기존 perf.3 ZIP/SHA와 체크포인트를 보존했고 perf.3 ZIP 해시는 변경되지 않았다.

실제 배포 EXE를 사용하는 7개 검증 명령이 모두 exit 0으로 완료됐다.

| 검사 | 결과/증적 폴더 |
| --- | --- |
| 오프라인 미리보기·API | 5종 코드 블럭 API, 기존 4종 × 4개 화면 폭 통과 · `offline-preview-NNandR` |
| 코드 블럭 UI·진단·다운로드 | 5종·근거·편집·복원·자체 검사 보고서·1440/390px 통과 · `code-block-ui-NjdcXz` |
| 공유 의미 기본 설정 | 위 표의 요청 수/페이지 수 및 자체 검사 3종 통과 · `shared-semantics-qO1Ahs` |
| 공유 의미 60,000자 | 동일 요청 수/페이지 수 및 자체 검사 3종 통과 · `shared-semantics-4n3wTT` |
| LLM 전송 제한 모드 | 합성 검사 5개 통과 · `packaged-llm-H2J3HN` |
| LLM 전송 기본 모드 | 합성 검사 5개 통과 · `packaged-llm-qB0rCc` |
| Windows 실행기 | 시작 정책·실패 보고서 보존·0이 아닌 종료 코드 등 7개 통과 · `windows-launchers-6O2mcM` |

배포본의 1440/390px 검사 화면과 모바일 오류 진단 캡처를 직접 확인했다.
`artifacts/perf4-validation/`에 선별 증적 161개와 재현용 검사 스크립트를 보존한다.
`package-audit.json`은 ZIP 감사, `source-inventory.json`은 검증 소스,
`evidence-inventory.json`은 증적 해시, `perf4-package-results.json`은 7개 명령 종료 결과다.
PowerShell 로그는 내용 보존 후 UTF-8로 정규화했다.

`.git` 없는 비동기화 작업본에서 빌드했으므로 manifest의 sourceCommit은 `unavailable`,
sourceTreeDirty는 null이다. 기준 커밋 이후의 정확한 소스는 위 해시 목록으로 식별한다.
이 재개 작업의 로컬 구현·검증·패키지 전달은 완료됐고 미해결 로컬 검사 실패는 없다.
변경 사항은 작업 트리에 보존했으며 이번 재개에서는 커밋/원격 푸시를 수행하지 않았다.

## 재현 명령

기존 비동기화 작업본 `%LOCALAPPDATA%\Temp\DiagramMaker-performance-20260910`에서 실행한다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\build-offline-win-x64.ps1 -Version '0.1.0-perf.4' -SkipTests
$package = 'artifacts/stage/DiagramMaker-0.1.0-perf.4-win-x64'
node scripts/smoke-offline-preview.mjs $package
node scripts/smoke-code-block-ui.mjs $package
node scripts/smoke-shared-semantics.mjs $package
node scripts/smoke-shared-semantics.mjs $package --characters-60000
node scripts/smoke-packaged-llm.mjs $package
node scripts/smoke-packaged-llm.mjs $package --basic
node scripts/smoke-windows-launchers.mjs $package
```

기존 ZIP을 덮어쓰지 않는다. 같은 버전이 존재하면 새 시험 버전을 지정한다.
