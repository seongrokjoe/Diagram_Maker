# FOUP 실패 경로 근거 연결 복구 (internal.17)

## 현장 증상

실제 GLM 5.2 실행에서 정상 이동 페이지는 완료됐으나 BPort 미검출, STR Pick 실패, Shelf 미검출 페이지의 노드·연결에 `requirementIds: []`가 반환됐다. 서버는 이를 일반 `NaturalNodeInvalid`·`NaturalEdgeInvalid`로 기록하고 반복 보정 후 `NaturalRepairNoProgress`로 종료했다. 소유자 비교 화면도 빈 근거 목록을 요소 부재로 표시했다.

## 변경

- 노드와 연결의 누락·알 수 없는 근거 ID를 별도 오류 코드, 대상 ID, 필드로 기록한다.
- 새 오류 코드와 근거 보정 단계는 진단 화면과 서버 종료 메시지에서 해당 원인을 설명한다.
- Sequence 참여자는 직접 연결된 명시적 요구사항에서 근거 ID를 계산한다. 남은 근거 오류는 기존 그래프의 해당 요소만 대상으로 한 보정 요청을 한 번 수행한다. 보정 응답은 대상·ID·중복·출처를 검사하며, 관련 없는 ID를 억지로 붙이지 않도록 빈 목록도 허용한다. 근거가 없는 요소는 계속 검증 실패로 남긴다.
- 비교 자료와 화면은 실제 요소 존재 여부와 비어 있는 근거 ID 목록을 구분한다. 빈 목록에서는 연결되지 않은 원문을 근거로 제시하지 않고 범위를 특정할 수 없다고 표시한다. 비교 본문은 기존 소유자 전용 경로와 비밀 마스킹을 유지한다.
- 생성 계약을 `natural-design-v9`, 생성기 버전을 `natural-v13`으로 갱신했다. 기존 결과와 수동 편집은 변환하지 않는다.
- 2026-09-29 재개 보완: 첫 생성에서 근거 전용 보정을 완료한 결과도 `DesignQuality.RepairUsed=true`로 기록한다. 정상 생성은 false를 유지한다. 참여자 근거에 가정 연결이 포함되지 않는 회귀와 근거 보정 성공·미해결의 API/UI 검사를 추가했다.

## 검증

2026-09-29 비동기화 검증 사본에서 전체 `verify.ps1 -UseInstalledDependencies`가 exit 0이었다. .NET 438개, Node 작업자 43개, 웹 64개, 정책 13개, 시작 정책 11개, 프런트엔드 빌드, API 두 모드, 공유 의미 기본·60,000자, SVG·Sequence·코드·디자인 UI, 자연어 합성 API/UI 23개, Windows UI 실행기 14개가 통과했다. FOUP 세 실패 분기, 근거 전용 보정, 보정 사용 표시, 가정 연결 제외 및 빈 근거 비교 상태를 회귀 테스트로 확인했다.

최종 internal.17 Windows ZIP은 96,577,426바이트, 1,832개 파일이다. ZIP의 모든 파일을 stage와 개별 SHA-256으로 대조했고 일치했다. ZIP SHA-256은 `1dc49d6a5a76985fcb4e3f477208574674afe1f6b35a137e21abad828b9ca5e6`이다. 최종 패키지에서 미리보기, 코드·디자인·자연어 UI, LLM 기본·간편, 공유 의미 기본·60,000자, Windows CMD 실행기 등 9개 검사 명령 모두 exit 0이었다. 자연어 API/UI 23개와 Windows CMD 실행기 8개 검사도 포함한다.

원본과 검증 사본의 입력 320개를 SHA-256으로 대조했다. 처음에는 긴 임시 경로로 인해 271자 NuGet 라이선스 파일 복사가 실패했으나 짧은 비동기화 경로에서 동일 소스의 빌드가 정상 완료됐다. 검증 정책은 유지했다. 재개 검증 로그·무결성 결과·입력 해시·390/1440px 화면은 `artifacts/internal17-resume-validation/`에 보관한다. 9월 23일 증적은 `artifacts/internal17-validation/`에 보존했고, 재개 전 internal.17 ZIP/SHA도 재개 검증 폴더에 따로 보관했다.

## 현장 확인

이 PC에는 사내 GLM 5.2와 PostgreSQL 연결 정보가 없어 실제 FOUP 입력의 최종 의미 품질과 응답 시간은 확인하지 못했다. 사내 PC에서 Thinking OFF로 동일 FOUP 입력을 새 실행하고, 세 실패 경로의 완료 페이지, 조건·극성·순서·근거 ID 및 소유자 비교 화면을 확인해야 한다. 근거 ID가 붙었다는 사실만으로 의미 정확성을 판정하지 않는다.

2026-09-29 재개 시 사용자가 실제 GLM 5.2·PostgreSQL 검증은 사내 PC에서 별도로 진행한다고 확인했다. 이번 재개에서는 로컬 구현, 합성 회귀, Windows 배포 및 Git 반영을 마무리한다.

배포 파일은 `artifacts/release/DiagramMaker-0.1.0-internal.17-win-x64.zip`과 같은 이름의 `.sha256` 파일이다. 비동기화 검증 사본에서 빌드했으므로 manifest의 sourceCommit은 `unavailable`이다. 이전 internal.16 ZIP/SHA는 보존했다.
