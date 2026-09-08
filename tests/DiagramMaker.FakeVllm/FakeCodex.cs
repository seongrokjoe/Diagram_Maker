using System.Text.Json;
using System.Text.Json.Nodes;

// Offline CLI test double, never an external model or a diagram quality oracle.
internal static class FakeCodex
{
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.FirstOrDefault() == "login") { Console.Error.WriteLine("FAKE: Logged in using ChatGPT"); return true; }
        if (args.FirstOrDefault() == "features")
        {
            foreach (var feature in ("shell_tool unified_exec shell_snapshot apps plugins hooks multi_agent multi_agent_v2 browser_use " +
                "browser_use_external browser_use_full_cdp_access computer_use in_app_browser in_app_chat in_app_local_automation view_image " +
                "image_generation code_mode_host memories skill_search skill_mcp_dependency_install workspace_dependencies remote_plugin tool_suggest skip_host_skill_discovery").Split(' '))
                Console.WriteLine($"{feature} stable false");
            return true;
        }
        if (args.FirstOrDefault() != "exec") return false;
        if (args.Contains("--help")) { Console.WriteLine("--output-schema --ignore-user-config --ephemeral"); return true; }
        var text = await Console.In.ReadToEndAsync();
        if (text.Contains("LOCAL_MANUAL_TEXT_NEVER_TO_MODEL", StringComparison.Ordinal) || text.Contains("UNAPPROVED_INPUT_MUST_NOT_LEAVE", StringComparison.Ordinal))
            throw new InvalidOperationException("An unapproved test marker reached the model input boundary.");
        if (text.Contains("SIMULATE_TIMEOUT", StringComparison.Ordinal)) await Task.Delay(TimeSpan.FromMinutes(10));
        if (text.Contains("SIMULATE_MALFORMED", StringComparison.Ordinal)) { Console.WriteLine("bad-json"); return true; }
        var firstBrace = text.IndexOf('{');
        var envelope = JsonNode.Parse(text[firstBrace..])!;
        var inputText = envelope["input"]!.GetValue<string>();
        JsonNode? input;
        try { input = JsonNode.Parse(inputText); } catch (JsonException) { input = null; }
        var schemaIndex = Array.IndexOf(args, "--output-schema");
        var response = "OK";
        if (schemaIndex >= 0)
        {
            var schema = JsonNode.Parse(await File.ReadAllTextAsync(args[schemaIndex + 1]))!;
            response = Complete(schema["properties"]!.AsObject(), input).ToJsonString();
        }
        // Real CLI 0.153.4 emits these nonfatal diagnostics before successful turns.
        Console.WriteLine(JsonSerializer.Serialize(new { type = "item.completed", item = new
        { type = "error", message = "FAKE: Code mode host is disabled." } }));
        Console.WriteLine(JsonSerializer.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = response } }));
        Console.WriteLine(JsonSerializer.Serialize(new { type = "turn.completed", usage = new { input_tokens = 100, output_tokens = 50 } }));
        return true;
    }

    private static JsonNode Complete(JsonObject properties, JsonNode? input)
    {
        if (properties.ContainsKey("accepted")) return JsonSerializer.SerializeToNode(new { accepted = true, issues = Array.Empty<string>() })!;
        if (properties.ContainsKey("result")) return JsonSerializer.SerializeToNode(new { result = "ok" })!;
        if (properties.ContainsKey("changes") && !properties.ContainsKey("elements"))
        {
            var facts = input!["facts"]!.AsArray();
            var ids = facts.SelectMany(fact => fact!["changeIds"]!.AsArray().Select(id => id!.GetValue<string>())).Distinct();
            return JsonSerializer.SerializeToNode(new { summary = "합성 변경 설명", changes = ids.Select(id => new
            {
                changeId = id, summary = "합성 입력 검증과 저장 경로 변경",
                factIds = facts.Where(fact => fact!["changeIds"]!.AsArray().Any(value => value!.GetValue<string>() == id)).Select(fact => fact!["id"]!.GetValue<string>()).ToArray()
            }).ToArray() })!;
        }
        if (properties.ContainsKey("elements"))
        {
            var context = input?["context"] ?? input!;
            var candidate = context["candidate"]!;
            var facts = context["facts"]!.AsArray();
            var byId = facts.ToDictionary(fact => fact!["id"]!.GetValue<string>());
            string[] ChangesFor(JsonNode? element) => (element?["sourceFactIds"]?.AsArray() ?? [])
                .SelectMany(id => byId.TryGetValue(id!.GetValue<string>(), out var fact)
                    ? fact!["changeIds"]!.AsArray().Select(value => value!.GetValue<string>()) : []).Distinct().ToArray();
            var changeIds = candidate["nodes"]!.AsArray().Concat(candidate["edges"]!.AsArray()).SelectMany(ChangesFor).Distinct().ToArray();
            return JsonSerializer.SerializeToNode(new
            {
                summary = "합성 CLI 계약 테스트: " + candidate["title"]!.GetValue<string>(),
                elements = candidate["nodes"]!.AsArray().Select(node => new
                {
                    id = node!["id"]!.GetValue<string>(), summary = "합성 동작",
                    nodeIds = new[] { node["id"]!.GetValue<string>() }
                }).ToArray(),
                messages = candidate["edges"]!.AsArray().Select(edge => new { edgeId = edge!["id"]!.GetValue<string>(), summary = "합성 호출" }).ToArray(),
                changes = changeIds.Select(id => new { changeId = id, summary = "합성 계약 검사: 변경 전후 근거 연결 (실제 모델 품질 평가 아님)",
                    factIds = facts.Where(fact => fact!["changeIds"]!.AsArray().Any(value => value!.GetValue<string>() == id)).Select(fact => fact!["id"]!.GetValue<string>()).ToArray(),
                    nodeIds = candidate["nodes"]!.AsArray().Where(node => ChangesFor(node).Contains(id)).Select(node => node!["id"]!.GetValue<string>()).ToArray(),
                    edgeIds = candidate["edges"]!.AsArray().Where(edge => ChangesFor(edge).Contains(id)).Select(edge => edge!["id"]!.GetValue<string>()).ToArray()
                }).ToArray(),
                instructionResults = new[] { "가짜 CLI 계약 테스트이며 LLM 품질 평가는 아님" }
            })!;
        }
        if (properties.ContainsKey("groups")) return JsonSerializer.SerializeToNode(new
        {
            groups = new[] { new { title = "합성 변경", description = "합성 변경 묶음", suggestedDiagramType = "flowchart",
                changeIds = input!["candidates"]!.AsArray().Select(candidate => candidate!["id"]!.GetValue<string>()).ToArray() } }
        })!;
        if (properties.ContainsKey("risks")) return JsonSerializer.SerializeToNode(new
        { summary = "합성 변경 요약", intent = "합성 경로 검증", risks = Array.Empty<object>(), warnings = new[] { "FAKE CLI: not real model quality" } })!;
        foreach (var (nodes, relations) in new[] { ("participants", "messages"), ("nodes", "flows"), ("classes", "relations"), ("states", "transitions") })
        {
            if (!properties.ContainsKey(relations) || !properties.ContainsKey(nodes)) continue;
            var result = new JsonObject
            {
                ["title"] = "합성 CLI 계약 테스트", [nodes] = new JsonArray("OrderService", "OrderStore"),
                [relations] = new JsonArray(new JsonObject { ["source"] = "OrderService", ["target"] = "OrderStore", ["label"] = "저장 요청", ["type"] = null }),
                ["notes"] = new JsonArray("FAKE CLI: not real model quality")
            };
            if (properties.ContainsKey("initialState")) result["initialState"] = "OrderService";
            return result;
        }
        return new JsonObject();
    }
}
