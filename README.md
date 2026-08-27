# AI Git Architecture Reviewer

사내 Git commit과 사내 LLM만 사용하여 구조 변경을 분석하고 Mermaid 다이어그램을 생성하는 내부 웹 애플리케이션입니다. 외부 LLM fallback, CDN, telemetry, 임의 Git URL, repository build 실행을 지원하지 않습니다.

## 구현된 기능

- 자연어 요청 → 구조화 DiagramIR → 안전한 Mermaid 렌더링
- 내부 OpenAI-compatible LLM Adapter와 개발 전용 결정적 생성기
- 내 PC의 로컬 Git 절대 경로 또는 `.git` 경로를 연결해 Base/Target SHA 비교
- Add/Delete/Modify 및 동일 blob rename 식별
- C# Roslyn syntax 분석과 C++ low-confidence fallback 분석
- commit 독립 `SymbolIdentity`, revision별 `SymbolVersion`, blob 기반 Evidence
- LLM 장애 시 정적 분석 결과를 유지하는 `Partial` 완료
- 재시작 후에도 등록 목록을 유지하는 로컬 JSON 저장소와 PostgreSQL lease queue
- reverse-proxy identity, repository role ACL, source-free audit metadata
- React 기반 자연어 생성, Git 분석, 저장소 관리 화면

## 개인 PC에서 실행

필수 도구는 .NET 9 SDK와 Node.js 24입니다. PowerShell에서 프로젝트 루트로 이동한 다음 아래 스크립트 하나를 실행합니다. 필요한 의존성과 변경된 프론트엔드·백엔드를 준비하고, API와 UI를 `127.0.0.1`에만 바인딩한 뒤 브라우저를 엽니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-local.ps1
```

접속 주소는 `http://localhost:5080`입니다. 종료할 때는 다음 스크립트를 사용합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\stop-local.ps1
```

저장소 관리 화면에서 `C:\Work\Git\MyRepository` 같은 저장소 루트나 `C:\Work\Git\MyRepository\.git` 경로를 붙여넣고 **연결 테스트** 후 등록합니다. 등록 정보는 Git에서 제외되는 `data/repositories.json`에 저장되어 앱을 재시작해도 유지됩니다. 네트워크 공유 경로와 Git URL은 받지 않으며, 저장소의 build·hook·checkout을 실행하지 않습니다.

프론트엔드를 수정하며 HMR이 필요할 때만 별도 터미널에서 `npm.cmd run dev --prefix .\web`을 실행하고 `http://localhost:5173`을 사용합니다.

## 사내 LLM 연결

운영 설정 또는 환경변수로 다음 값을 제공합니다.

```text
Llm__Enabled=true
Llm__BaseUrl=https://internal-llm.company.local
Llm__ChatPath=/v1/chat/completions
Llm__Model=internal-code-model
Llm__AllowedHosts__0=internal-llm.company.local
Llm__ApiKey=<vault injection>
```

Base URL의 host가 allowlist와 일치하지 않으면 앱이 시작되지 않습니다. Redirect와 환경 proxy도 사용하지 않습니다.

## 배포

`.env.example`을 참고해 비밀정보를 vault 또는 배포 시스템에서 주입한 다음 사내 registry에 build한 image를 배포합니다.

```powershell
docker compose build
docker compose up -d
```

Production에서는 앱을 OIDC reverse proxy 뒤에 배치하고 proxy가 `X-Remote-User`, `X-Remote-Roles`를 설정해야 합니다. 사용자 PC에는 .NET, Node, Git, Clang 또는 LLM credential이 필요하지 않습니다.

## 검증

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

검증은 backend unit test, 실제 임시 Git repository 비교, frontend production build, vulnerability audit와 NPM license allowlist를 포함합니다.

## 현재 경계

- C++ 분석은 Clang Worker가 연결되기 전까지 정규식 기반 `Inferred` 결과입니다.
- 전체 repository baseline/incremental graph가 아니라 변경 파일 내 Symbol과 관계를 우선 분석합니다.
- Working Tree, submodule, Git LFS, SSH clone, MR hook, Excalidraw/PlantUML은 포함하지 않습니다.
- 운영 배포 전 회사 오픈소스·보안 담당자의 SBOM 및 network policy 승인이 필요합니다.
