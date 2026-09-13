# UI 디자인 개선 결과

2026-09-12 · 디자인 중심 변경. 기존 accuracy.1/perf.4 작업과 패키지를 보존했다.

## 반영 내용

- 공통: 밝은 배경, 얇은 테두리, 파란 강조색, 작은 간격과 역할별 글자 크기/굵기. 버튼과 입력 필드의 스타일을 통일했다.
- 폰트: Pretendard Variable 1.3.9를 앱에 동봉했다. 설치된 폰트나 외부 CDN 없이 같은 한글 서체를 사용한다. 코드 입력의 고정폭 서체와 다이어그램 렌더링 서체는 유지했다.
- 자연어: 샘플은 한 열에 세로로, 각 샘플 안에서는 이미지/설명을 가로로 배치했다. 다이어그램 종류 라벨과 선택 메뉴도 가로로 정렬했다. 다른 화면의 샘플은 기존 격자를 유지한다.
- 코드 블럭: 상단 간격을 줄이고 저장한 작업 선택 폭을 360~560px로 넓혔다. 그룹 제목·옵션·사용자 관계·블럭·출력 설정을 하나의 그룹 컨테이너 안에 배치했다. 선택된 탭은 파란색으로 강조하고 결과 트리 각 행에 구분선을 넣었다.
- Git: 커밋 목록을 선택 시 닫히는 검색 드롭다운으로 변경했다. 목록 높이 300px, 50개 추가, SHA 직접 입력을 유지한다. 화살표/Enter/Escape와 바깥 클릭·포커스 이탈을 지원하며 저장소 전환 후 늦게 도착한 응답은 무시한다. 그룹화와 결과 화면도 촘촘하게 정리했다.
- 반응형: 1366, 1440, 1920, 800, 390px에서 확인했다. 좁은 화면은 세로 배치로 전환하며 긴 이름은 잘림/줄바꿈을 사용한다.

서버 API, 저장 형식, 분석 알고리즘은 이번 디자인 작업에서 변경하지 않았다. 결과 트리의 계층/선택/접기 상태와 생성 이력 드롭다운도 유지한다.

## 검증

보안 지침에 따라 빌드와 실행은 OneDrive 밖의 `C:\Users\고정현\AppData\Local\Temp\DiagramMaker-ui-20260912` 복사본에서 수행했다.
Browser 연결이 제공되지 않아 기존 저장소의 격리 Headless Edge 검증 방식을 사용했다.

| 항목 | 결과 |
| --- | --- |
| .NET | 279개 통과 |
| Git 작업자 | 37개 통과 |
| 웹 단위 테스트 | 46개 통과 |
| 정책/폰트 | 8개 통과 |
| TypeScript/프런트엔드 빌드 | 통과 |
| 자연어·Git UI | 샘플 선택, 동봉 폰트 실제 로딩, 커밋 검색/추가/SHA/키보드/지연 응답, 그룹화/결과 및 5개 폭 통과 |
| 코드 블럭 UI | 긴 작업명·그룹명, 관계 3개, 그룹 내부 블럭, 탭·트리·편집·다운로드·재진입 회귀 및 5개 폭 통과 |
| 전체 verify | 종료 코드 0. API 기본/제한 모드, 공유 의미 2설정, SVG 6개 및 UI 검사 통과 |
| Windows 패키지 | `0.1.0-ui.1` 빌드·가드·SBOM 통과. 실제 EXE에서 디자인/코드 블럭 UI, 4종×4개 폭 미리보기 및 내장 작업자 API 통과 |

확인 중 발견한 커밋 팝업의 로딩 버튼 포커스 이탈 문제를 수정했다. 검증 실행기의 상대 경로가 OneDrive로 해석되던 문제는 실제 작업 디렉터리를 Temp로 지정해 해결했으며 보안 정책은 완화하지 않았다.
실행기가 분리된 하위 프로세스까지 기다리던 문제도 직접 프로세스 종료를 확인하는 방식으로 해결했다. 최종 전체 검증을 다시 실행해 종료 코드 0을 확인했다. 미해결 로컬 실패는 없다.
사내 LLM/DB 실연결과 외부 취약점 피드 검사는 실행하지 않았다. Git 화면 데이터는 합성 픽스처이며 실제 사내 분석 품질 검증과 구분한다.

## 폰트와 배포

- 원본 및 고지: `web/src/assets/fonts/`의 WOFF2, LICENSE, manifest.
- WOFF2 SHA-256: `9599f12fd42fc0bce1cd50b47a0c022e108d7aa64dd0d1bb0ed44f3282d900b4`.
- 빌드 전 폰트/고지 해시 확인, 배포 라이선스 수집, CycloneDX SBOM, 배포 폰트 해시 검사에 연결했다.
- OFL-1.1 허용은 이 버전의 폰트 자산에만 한정하며 소프트웨어 의존성 허용 목록은 넓히지 않았다.
- 검증용 Windows ZIP은 Temp 작업본의 `artifacts/release/DiagramMaker-0.1.0-ui.1-win-x64.zip`에 보존했다(96,345,461바이트). 기존 배포 ZIP은 교체하지 않았다.
- 원클릭 UI 실행기 후속 작업에서 같은 ZIP과 SHA를 저장소의 `artifacts/release/`에도 추가했다. `start-ui-test.cmd`로 실행하고 `stop-ui-test.cmd`로 종료한다. 사용법은 [UI_TEST_KO.md](UI_TEST_KO.md)를 참조한다.
- ZIP SHA-256: `f7c831588245eac3cd70d26674d28079109eeaa4bcbe7b836e288fe03582003e`.
- 패키지의 JS/CSS/폰트/고지는 전체 검증용 빌드와 바이트 단위로 일치한다. 배포 SBOM은 281개 구성요소를 포함한다.

## 화면 증적

최종 검사 로그와 선별 캡처는 `artifacts/ui-design/`에 보존한다. 커밋과 원격 푸시는 수행하지 않았다.
현재 소스 273개 파일이 검증 작업본과 일치하며, 실제 배포 EXE의 캡처 74개를 보존했다.
상세 대조 결과는 [validation.json](artifacts/ui-design/validation.json), 검증 요약은 [result-summary.json](artifacts/ui-design/result-summary.json)에 있다.

- [자연어 1440px](artifacts/ui-design/screenshots/design-ui/natural-1440.png)
- [코드 구성 — 긴 제목과 사용자 관계 3개](artifacts/ui-design/screenshots/code-block-ui/compose-1440.png)
- [코드 결과 트리 390px](artifacts/ui-design/screenshots/code-block-ui/workspace-390.png)
- [Git 커밋 드롭다운](artifacts/ui-design/screenshots/design-ui/git-commits-open-1440.png)
- [Git 그룹화](artifacts/ui-design/screenshots/design-ui/git-grouping-1440.png)
- [Git 결과](artifacts/ui-design/screenshots/design-ui/git-results-1440.png)

가장 최근의 검증용 빌드로 앱을 다시 시작해야 실제 사용 화면에 변경이 반영된다. 검증은 합성 데이터와 격리 서버에서 실행했고 기존 사용자 실행 환경을 교체하지 않았다.
