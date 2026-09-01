# AI Git Architecture Reviewer

사내 Git commit과 사내 LLM만 사용하여 구조 변경을 분석하고 Mermaid 다이어그램을 생성하는 내부 웹 애플리케이션입니다. 외부 LLM fallback, CDN, telemetry, 임의 Git URL, repository build 실행을 지원하지 않습니다.

## 구현된 기능

- 자연어 요청 → 형식별 의미 계약 → 결정론적 DiagramIR 정규화 → 안전한 Mermaid 렌더링
- 같은 자연어 요청의 세션 내 결과 재사용, 명시적 LLM 재생성, 영속 리비전 이력
- Mermaid 지연 로딩, 제한형 DSL 편집과 검증, SVG/PNG 다운로드
- 인증 없는 사내 vLLM 전용 Adapter, 구조화 출력 fallback, Thinking 요청 선택
- 내 PC의 로컬 Git 절대 경로 또는 `.git` 경로를 연결해 Base/Target SHA 비교
- 설치된 네이티브 Git 우선 처리와 `isomorphic-git` 자동 fallback
- Add/Delete/Modify 및 동일 blob rename 식별
- C# Roslyn syntax 분석과 C++ low-confidence fallback 분석
- commit 독립 `SymbolIdentity`, revision별 `SymbolVersion`, blob 기반 Evidence
- LLM 장애 시 정적 분석 결과를 유지하는 `Partial` 완료
- 재시작 후에도 등록 목록을 유지하는 로컬 JSON 저장소와 PostgreSQL lease queue
- reverse-proxy identity, repository role ACL, source-free audit metadata
- React 기반 자연어 생성, Git 분석, 저장소 관리, 합성 LLM 연결 점검 화면

## 개인 PC에서 실행

필수 도구는 .NET 9 SDK와 Node.js 24입니다. PowerShell에서 프로젝트 루트로 이동한 다음 아래 스크립트 하나를 실행합니다. 필요한 의존성과 변경된 프론트엔드·백엔드를 준비하고, API와 UI를 `127.0.0.1`에만 바인딩한 뒤 브라우저를 엽니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

접속 주소는 `http://localhost:5080`입니다. 종료할 때는 다음 스크립트를 사용합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\stop-local.ps1
```

저장소 관리 화면에서 `C:\Work\Git\MyRepository` 같은 저장소 루트나 `C:\Work\Git\MyRepository\.git` 경로를 붙여넣고 **연결 테스트** 후 등록합니다. 등록 정보는 Git에서 제외되는 `data/repositories.json`에 저장되어 앱을 재시작해도 유지됩니다. 네트워크 공유 경로와 Git URL은 받지 않으며, 저장소의 build·hook·checkout을 실행하지 않습니다. Git 변경 분석은 PATH의 `git` 명령을 우선 사용하며, 실행 파일이 없을 때만 포함된 `isomorphic-git` Worker로 전환합니다.

프론트엔드를 수정하며 HMR이 필요할 때만 별도 터미널에서 `npm.cmd run dev --prefix .\web`을 실행하고 `http://localhost:5173`을 사용합니다.

## 사내 LLM 연결

실제 endpoint와 모델은 저장소가 아닌 `%LOCALAPPDATA%\DiagramMaker\llm-policy.json`에 둡니다. `packaging\windows\config\llm-policy.example.json`을 복사한 뒤 승인된 값으로 수정합니다.

```json
{
  "Llm": {
    "Enabled": true,
    "Endpoint": "https://llm.invalid/v1/chat/completions",
    "AllowedOrigin": "https://llm.invalid",
    "Model": "approved-model",
    "AllowDevelopmentStub": false,
    "ThinkingOutputTokens": 60000,
    "OutputHardLimit": 60000
  }
}
```

Endpoint와 AllowedOrigin의 scheme, host, port가 정확히 일치하지 않으면 앱이 시작되지 않습니다. 현재 승인 계약은 API Key 없이 `structured_outputs.json`과 `chat_template_kwargs.enable_thinking`을 사용합니다. Redirect, cookie, 기본 자격 증명과 환경 proxy도 사용하지 않습니다.

웹의 **사내 LLM 점검** 탭은 프로젝트 데이터를 보내지 않고 기본 연결, DiagramIR 구조화, Thinking 프로토콜을 차례로 확인합니다. 2단계는 DiagramIR 무결성을 검사하고 3단계는 작은 고정 JSON으로 Thinking과 structured output의 결합만 독립 검증합니다. 자연어 생성과 Git LLM 요약에서는 요청별 Thinking 체크박스를 사용할 수 있습니다. 자연어 생성은 `temperature=0`을 기본값으로 사용하며, `Llm:NaturalDiagramSeed`를 지원하는 사내 모델에는 고정 seed도 지정할 수 있습니다.
Thinking 요청의 `max_tokens`는 reasoning과 최종 JSON을 합친 생성 상한이며 `ThinkingOutputTokens`를 사용합니다. `MaxInputCharacters`는 토큰이 아닌 입력 문자 수 제한입니다.

## 배포

`.env.example`을 참고해 비밀정보를 vault 또는 배포 시스템에서 주입한 다음 사내 registry에 build한 image를 배포합니다.

```powershell
docker compose build
docker compose up -d
```

Production에서는 앱을 OIDC reverse proxy 뒤에 배치하고 proxy가 `X-Remote-User`, `X-Remote-Roles`를 설정해야 합니다. 사용자 PC에는 .NET, Node, Clang 또는 LLM credential이 필요하지 않습니다. Git은 선택 사항이지만 대용량 pack 저장소 분석에는 승인된 네이티브 Git 설치를 권장합니다.

외부 npm 접속 또는 GitHub Release asset 다운로드가 차단된 Windows x64 PC에서는 Source code ZIP 안의 `artifacts/release/DiagramMaker-*-win-x64.zip`을 사용합니다. 이 패키지는 self-contained .NET, portable Node.js, 웹 UI와 Git Worker 의존성을 포함하며 CMD에서 `configure-llm.cmd`, `start.cmd`, `test-llm.cmd` 순서로 실행합니다.

## 검증

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

CMD만 허용되는 환경에서는 인터넷과 승인된 패키지 registry가 있는 개발 PC에서 다음을 사용할 수 있습니다.

```bat
scripts\verify.cmd
```

검증은 backend unit test, loopback Fake vLLM, 실제 임시 Git repository 비교, frontend production build, vulnerability audit와 NPM license allowlist를 포함합니다. 자동 검증은 실제 사내 LLM에 접속하지 않습니다.

## 현재 경계

- C++ 분석은 Clang Worker가 연결되기 전까지 정규식 기반 `Inferred` 결과입니다.
- 전체 repository baseline/incremental graph가 아니라 변경 파일 내 Symbol과 관계를 우선 분석합니다.
- Working Tree, submodule, Git LFS, SSH clone, MR hook, Excalidraw/PlantUML은 포함하지 않습니다.
- 운영 배포 전 회사 오픈소스·보안 담당자의 SBOM 및 network policy 승인이 필요합니다.
