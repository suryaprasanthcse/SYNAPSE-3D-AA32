using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MAAYAI.HackMatrix
{
    /// <summary>Everything needed to talk to one OpenAI-compatible chat completions endpoint.</summary>
    [Serializable]
    public struct LlmConfig
    {
        public string endpointUrl;
        public string apiKey;
        public string model;
        public float temperature;
        public float timeoutSeconds;
        public bool jsonMode;
        /// <summary>Run the agent as a tool-calling loop (calculate_flight_path) instead of plain JSON replies.</summary>
        public bool useTools;
    }

    /// <summary>One function call the model asked for.</summary>
    public sealed class ToolCall
    {
        public string Id;
        public string Name;
        public string Arguments;        // JSON text, exactly as the model wrote it
    }

    /// <summary>
    /// A message in a tool-calling conversation. Serialized by hand: JsonUtility would emit every field on every
    /// message (an empty tool_call_id on a user turn, an empty tool_calls array on a plain reply), which strict
    /// endpoints reject.
    /// </summary>
    public sealed class AgentMessage
    {
        public string Role;
        public string Content;
        /// <summary>For role "tool": the call this message answers.</summary>
        public string ToolCallId;
        /// <summary>For role "assistant": the model's tool_calls array, VERBATIM from its reply. Echoed back
        /// unchanged so provider-specific fields inside it (Gemini's thought signatures) survive the round trip.</summary>
        public string RawToolCalls;

        public AgentMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }

        public string ToJson()
        {
            var sb = new StringBuilder("{\"role\":").Append(LlmChatClient.Quote(Role));
            sb.Append(",\"content\":").Append(Content == null ? "null" : LlmChatClient.Quote(Content));
            if (!string.IsNullOrEmpty(RawToolCalls)) sb.Append(",\"tool_calls\":").Append(RawToolCalls);
            if (!string.IsNullOrEmpty(ToolCallId)) sb.Append(",\"tool_call_id\":").Append(LlmChatClient.Quote(ToolCallId));
            return sb.Append('}').ToString();
        }
    }

    /// <summary>One message in a chat. Field names match the wire format, which JsonUtility maps verbatim.</summary>
    [Serializable]
    public sealed class ChatTurn
    {
        public string role;
        public string content;

        public ChatTurn() { }                           // for JsonUtility when reading replies

        public ChatTurn(string role, string content)
        {
            this.role = role;
            this.content = content;
        }
    }

    /// <summary>The outcome of one completion. Never null, never thrown: failures are data.</summary>
    public sealed class ChatResult
    {
        public bool Ok;
        public string Content;
        public string Error;
        public long HttpCode;
        public float LatencySeconds;
        /// <summary>Tool mode only: the calls the model made (empty when it replied with text).</summary>
        public readonly List<ToolCall> ToolCalls = new();
        /// <summary>Tool mode only: the reply's tool_calls array as raw JSON, for echoing back.</summary>
        public string RawToolCalls;
    }

    /// <summary>
    /// The one place a request leaves the device. Used by the runtime agent and by the Editor evaluation alike,
    /// so the evaluation measures the transport the phone actually uses.
    ///
    /// Task-based rather than a coroutine so it also runs in the Editor outside Play mode: continuations are
    /// posted to Unity's synchronisation context, which keeps UnityWebRequest on the main thread in both.
    /// </summary>
    public static class LlmChatClient
    {
        [Serializable] sealed class ResponseFormat { public string type = "json_object"; }

        [Serializable]
        sealed class Request
        {
            public string model;
            public ChatTurn[] messages;
            public float temperature;
        }

        [Serializable]
        sealed class RequestJson
        {
            public string model;
            public ChatTurn[] messages;
            public float temperature;
            public ResponseFormat response_format = new();
        }

        [Serializable] sealed class Response { public Choice[] choices; }
        [Serializable] sealed class Choice { public ChatTurn message; }

        [Serializable] sealed class WireFunction { public string name; public string arguments; }
        [Serializable] sealed class WireToolCall { public string id; public string type; public WireFunction function; }
        [Serializable] sealed class WireToolMessage { public string role; public string content; public WireToolCall[] tool_calls; }
        [Serializable] sealed class ToolResponse { public ToolChoice[] choices; }
        [Serializable] sealed class ToolChoice { public WireToolMessage message; }

        /// <summary>Plain completion: the reply's text in <see cref="ChatResult.Content"/>.</summary>
        public static Task<ChatResult> CompleteAsync(LlmConfig config, IReadOnlyList<ChatTurn> messages,
                                                     CancellationToken cancel = default) =>
            SendAsync(config, () => BuildBody(config, messages), false, cancel);

        /// <summary>
        /// Tool-calling completion: the model may answer with text, with tool calls, or both. The request carries
        /// the tool schema and tool_choice "auto"; response_format is left out, since JSON mode and tools do not
        /// mix on every provider.
        /// </summary>
        public static Task<ChatResult> CompleteWithToolsAsync(LlmConfig config, IReadOnlyList<AgentMessage> messages,
                                                              string toolsJson, CancellationToken cancel = default) =>
            SendAsync(config, () => BuildToolBody(config, messages, toolsJson), true, cancel);

        static async Task<ChatResult> SendAsync(LlmConfig config, Func<string> buildBody, bool tools, CancellationToken cancel)
        {
            var result = new ChatResult();
            float started = Time.realtimeSinceStartup;

            if (string.IsNullOrWhiteSpace(config.endpointUrl) || string.IsNullOrWhiteSpace(config.apiKey))
            {
                result.Error = "no endpoint or API key configured";
                return result;
            }

            // A malformed URL must fail as what it is. Passed through, it reaches the socket layer and comes back
            // as "ConnectionError (HTTP 0): Cannot connect to destination host" - which reads exactly like a
            // network or Android permission problem and sends the debugging the wrong way.
            if (!TryNormalizeEndpoint(config.endpointUrl, out string endpoint, out string endpointError))
            {
                result.Error = endpointError;
                return result;
            }
            config.endpointUrl = endpoint;

            UnityWebRequest request = null;
            try
            {
                // Everything that can throw synchronously - notably SendWebRequest on an http:// URL that Player
                // Settings forbids - is inside the try, so it becomes a result rather than an exception.
                try
                {
                    request = new UnityWebRequest(config.endpointUrl, UnityWebRequest.kHttpVerbPOST)
                    {
                        uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(buildBody())),
                        downloadHandler = new DownloadHandlerBuffer(),
                        timeout = Mathf.CeilToInt(Mathf.Max(config.timeoutSeconds, 1f))
                    };
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Authorization", "Bearer " + config.apiKey);
                    _ = request.SendWebRequest();      // polled below against our own deadline, not awaited
                }
                catch (Exception e)
                {
                    result.Error = e.Message.Contains("Insecure connection")
                        ? "http:// blocked by Player Settings > Other Settings > Allow downloads over HTTP"
                        : "request could not start: " + e.Message;
                    return result;
                }

                // Wall-clock deadline: UnityWebRequest.timeout is whole seconds and only covers the transport.
                float deadline = started + Mathf.Max(config.timeoutSeconds, 0.5f);
                while (!request.isDone)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        request.Abort();
                        result.Error = "cancelled";
                        return result;
                    }
                    if (Time.realtimeSinceStartup > deadline)
                    {
                        request.Abort();
                        result.Error = $"timed out after {config.timeoutSeconds:0.#} s";
                        return result;
                    }
                    await Task.Delay(20);
                }

                result.HttpCode = request.responseCode;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    result.Error = $"{request.result} (HTTP {request.responseCode}): {request.error}";
                    // The body is where the provider says *why* ("model not found", "API key invalid"); the
                    // status line alone does not distinguish a bad model name from a bad route.
                    string body = request.downloadHandler?.text;
                    if (!string.IsNullOrWhiteSpace(body))
                        result.Error += " | " + Shorten(body.Replace('\n', ' ').Replace('\r', ' ').Trim(), 240);
                    return result;
                }

                if (tools)
                {
                    if (!TryExtractToolReply(request.downloadHandler.text, result, out string toolError))
                    {
                        result.Error = toolError;
                        return result;
                    }
                    result.Ok = true;
                    return result;
                }

                if (!TryExtractContent(request.downloadHandler.text, out string content, out string error))
                {
                    result.Error = error;
                    return result;
                }

                result.Ok = true;
                result.Content = content;
                return result;
            }
            catch (Exception e)
            {
                result.Error = "transport failure: " + e.Message;
                return result;
            }
            finally
            {
                result.LatencySeconds = Time.realtimeSinceStartup - started;
                request?.Dispose();
            }
        }

        /// <summary>
        /// Recover the real URL from what people actually paste into an Inspector field: a Markdown link copied
        /// out of a chat window ("[https://x](https://x)"), angle brackets, quotes, backticks, stray whitespace,
        /// and invisible characters (zero-width spaces, a BOM, non-breaking spaces). Then require an absolute
        /// http(s) URL with a host. Returns false, with a message naming the bad value, if nothing usable remains.
        /// </summary>
        public static bool TryNormalizeEndpoint(string raw, out string url, out string error)
        {
            url = null;
            if (string.IsNullOrWhiteSpace(raw)) { error = "endpoint URL is empty"; return false; }

            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\u2060' || c == '\uFEFF') continue;
                sb.Append(c == '\u00A0' ? ' ' : c);
            }
            string s = sb.ToString().Trim();

            // [label](target): the target is the URL. Take the last one, in case the label is itself a link.
            int link = s.LastIndexOf("](", StringComparison.Ordinal);
            if (link >= 0)
            {
                int start = link + 2;
                int end = s.IndexOf(')', start);
                s = end > start ? s.Substring(start, end - start) : s.Substring(start);
            }

            s = s.Trim().Trim('<', '>', '"', '\'', '`', '[', ']', '(', ')', ' ', '\t', '\r', '\n');

            if (s.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0 ||
                !Uri.TryCreate(s, UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrEmpty(uri.Host))
            {
                error = $"endpoint URL is malformed: '{Shorten(raw, 80)}' (expected https://host/path)";
                return false;
            }

            url = s;
            error = null;
            return true;
        }

        static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 3) + "...";

        static string BuildBody(LlmConfig config, IReadOnlyList<ChatTurn> messages)
        {
            var array = new ChatTurn[messages.Count];
            for (int i = 0; i < array.Length; i++) array[i] = messages[i];

            // JsonUtility does the escaping; hand-built JSON breaks on the first quote in a prompt.
            return config.jsonMode
                ? JsonUtility.ToJson(new RequestJson { model = config.model, messages = array, temperature = config.temperature })
                : JsonUtility.ToJson(new Request { model = config.model, messages = array, temperature = config.temperature });
        }

        static string BuildToolBody(LlmConfig config, IReadOnlyList<AgentMessage> messages, string toolsJson)
        {
            var sb = new StringBuilder("{\"model\":").Append(Quote(config.model));
            sb.Append(",\"temperature\":").Append(config.temperature.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"messages\":[");
            for (int i = 0; i < messages.Count; i++) sb.Append(i == 0 ? "" : ",").Append(messages[i].ToJson());
            sb.Append("],\"tools\":").Append(toolsJson).Append(",\"tool_choice\":\"auto\"}");
            return sb.ToString();
        }

        /// <summary>A JSON string literal, escaped per RFC 8259.</summary>
        public static string Quote(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>choices[0].message of a tool-calling reply: its text (may be empty) and its tool calls.</summary>
        static bool TryExtractToolReply(string responseJson, ChatResult result, out string error)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) { error = "empty body"; return false; }

            ToolResponse response;
            try { response = JsonUtility.FromJson<ToolResponse>(responseJson); }
            catch (Exception e) { error = "body is not JSON: " + e.Message; return false; }

            var message = response?.choices != null && response.choices.Length > 0 ? response.choices[0].message : null;
            if (message == null) { error = "no message"; return false; }

            result.Content = message.content;
            if (message.tool_calls != null)
                foreach (var call in message.tool_calls)
                    if (call?.function != null && !string.IsNullOrEmpty(call.function.name))
                        result.ToolCalls.Add(new ToolCall { Id = call.id, Name = call.function.name, Arguments = call.function.arguments });

            if (result.ToolCalls.Count > 0)
            {
                result.RawToolCalls = ExtractRawValue(responseJson, "tool_calls");
                if (result.RawToolCalls == null)
                {
                    // Rebuilt without any provider extras: better than an assistant turn with no tool_calls at all.
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < result.ToolCalls.Count; i++)
                    {
                        var c = result.ToolCalls[i];
                        sb.Append(i == 0 ? "" : ",").Append("{\"id\":").Append(Quote(c.Id ?? "")).Append(",\"type\":\"function\",")
                          .Append("\"function\":{\"name\":").Append(Quote(c.Name)).Append(",\"arguments\":")
                          .Append(Quote(c.Arguments ?? "{}")).Append("}}");
                    }
                    result.RawToolCalls = sb.Append(']').ToString();
                }
            }
            if (result.ToolCalls.Count == 0 && string.IsNullOrWhiteSpace(result.Content))
            {
                error = "reply has neither text nor a tool call";
                return false;
            }
            error = null;
            return true;
        }

        /// <summary>
        /// The raw JSON text of the first value under <paramref name="key"/>: an object or array, found by
        /// bracket matching that skips over string literals. Null if absent or unbalanced.
        /// </summary>
        public static string ExtractRawValue(string json, string key)
        {
            // The quoted name can also appear as a VALUE ("finish_reason": "tool_calls" comes first in an OpenAI
            // reply), so only an occurrence followed by a colon is the key.
            string quoted = "\"" + key + "\"";
            int i = -1;
            for (int k = json.IndexOf(quoted, StringComparison.Ordinal); k >= 0;
                 k = json.IndexOf(quoted, k + quoted.Length, StringComparison.Ordinal))
            {
                int c = k + quoted.Length;
                while (c < json.Length && char.IsWhiteSpace(json[c])) c++;
                if (c >= json.Length || json[c] != ':') continue;
                c++;
                while (c < json.Length && char.IsWhiteSpace(json[c])) c++;
                if (c < json.Length && (json[c] == '[' || json[c] == '{')) { i = c; break; }
            }
            if (i < 0) return null;

            int depth = 0;
            bool inString = false;
            for (int j = i; j < json.Length; j++)
            {
                char c = json[j];
                if (inString)
                {
                    if (c == '\\') j++;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '[' || c == '{') depth++;
                else if ((c == ']' || c == '}') && --depth == 0) return json.Substring(i, j - i + 1);
            }
            return null;
        }

        /// <summary>choices[0].message.content from an OpenAI-style completion.</summary>
        public static bool TryExtractContent(string responseJson, out string content, out string error)
        {
            content = null;
            if (string.IsNullOrWhiteSpace(responseJson)) { error = "empty body"; return false; }

            Response response;
            try { response = JsonUtility.FromJson<Response>(responseJson); }
            catch (Exception e) { error = "body is not JSON: " + e.Message; return false; }

            content = response?.choices != null && response.choices.Length > 0 ? response.choices[0].message?.content : null;
            if (string.IsNullOrWhiteSpace(content)) { error = "no message content"; return false; }
            error = null;
            return true;
        }
    }

    /// <summary>What the planner is told, in one place, so the device and the evaluation send identical text.</summary>
    public static class PlannerPrompt
    {
        /// <summary>
        /// The system prompt. Footprints are generated from <see cref="SpatialRecipes"/> at the given kit scale,
        /// so the sizes the model is told are the sizes the verifier checks and the compiler builds.
        /// </summary>
        public const string ToolName = "calculate_flight_path";

        public static string System(float kitScale, float footprintMetres, bool toolMode = false)
        {
            float half = footprintMetres * 0.5f;
            var sizes = new StringBuilder();
            foreach (var type in SpatialNodeTypes.All)
            {
                Vector2 h = SpatialRecipes.HalfExtentDesign(type) * (2f * kitScale);
                sizes.Append(sizes.Length == 0 ? "" : ", ").Append($"{type} {h.x:0.00} by {h.y:0.00}");
            }
            float buffer = SpatialRecipes.LzBufferDesign * kitScale;
            float range = SpatialRecipes.RadioRangeDesign * kitScale;

            string output = toolMode
                ? $"Work by calling the {ToolName} tool with your complete zone map. The tool runs the drone's " +
                  "verifier and coverage planner and returns either VERIFIED with the planned route, or REJECTED with " +
                  $"every violation and how to fix it. When it is rejected, fix every violation and call {ToolName} " +
                  "again with the complete corrected zone map. Always call the tool; do not reply with the map as text. "
                : "Reply with ONE JSON object and nothing else: no prose, no markdown. Schema: " +
                  "{\"plan_name\": string, \"nodes\": [{\"node_type\": string, " +
                  "\"coordinates\": {\"x\": number, \"y\": number, \"z\": number}, \"tier\": integer}]}. ";

            return
                "You are an autonomous UAV disaster assessment agent. Turn the following emergency field report into " +
                "a structural zone map. You do NOT plan the flight: the drone's on-board planner computes the survey " +
                "path over your zones. " + output +
                $"Radio: the area has no working infrastructure. Every RescueLZ must link back to the LaunchPad by " +
                $"radio: the LaunchPad and each RelayNode reach {range:0.00} m (centre to centre), and a RescueLZ is " +
                "linked if it is within range of the LaunchPad or of a RelayNode that is itself linked. Place RelayNodes " +
                "in a chain to bridge longer gaps; a RescueLZ does not relay for others. " +
                $"node_type must be exactly one of: {string.Join(", ", SpatialNodeTypes.All)}. " +
                "LaunchPad: where the drone takes off; exactly one, on open ground near the edge of the area. " +
                "HazardZone: a critical collapse (collapsed, pancaked or burning structure; trapped people). " +
                "ShoringSite: a structure still standing but unstable (cracked, leaning, partly collapsed). " +
                "RescueLZ: clear open ground where a rescue helicopter can land (a field, car park or square); at " +
                "least one. RelayNode: a radio relay mast. Map EVERY damaged structure the report mentions " +
                "as its own HazardZone or ShoringSite. For HazardZone and ShoringSite, tier is the structure's storeys " +
                $"minus one (0 = single storey, {SpatialNodeTypes.MaxTier} = four or more); use 0 for other types. " +
                "Directions: +x is east, -x is west, +z is north, -z is south; the centre of the area is 0,0. " +
                $"coordinates are metres on a {footprintMetres:0.0} metre square map of the area, so x and z lie " +
                $"within -{half:0.00} and {half:0.00}, and every zone must fit inside it; y is ignored. coordinates " +
                "must be an object with x, y and z keys, never an array. " +
                $"Zone sizes in metres (x by z): {sizes}. Zones must not overlap. A RescueLZ must be at least " +
                $"{buffer:0.00} m from every HazardZone, edge to edge. Use between 3 and 16 nodes.";
        }

        /// <summary>The one tool, in the OpenAI function-calling schema. Its parameters ARE the zone map schema.</summary>
        public static string ToolsJson()
        {
            var types = new StringBuilder();
            foreach (var t in SpatialNodeTypes.All) types.Append(types.Length == 0 ? "" : ",").Append('"').Append(t).Append('"');

            return "[{\"type\":\"function\",\"function\":{" +
                   $"\"name\":\"{ToolName}\"," +
                   "\"description\":\"Verify a structural zone map against the drone's safety rules (bounds, overlap, " +
                   "landing-zone buffer, radio relay chain) and compute the boustrophedon survey path over it. Returns " +
                   "VERIFIED with route statistics, or REJECTED with every violation and its fix.\"," +
                   "\"parameters\":{\"type\":\"object\",\"properties\":{" +
                   "\"plan_name\":{\"type\":\"string\",\"description\":\"Short name for the zone map.\"}," +
                   "\"nodes\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{" +
                   $"\"node_type\":{{\"type\":\"string\",\"enum\":[{types}]}}," +
                   "\"coordinates\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"}," +
                   "\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\"]}," +
                   "\"tier\":{\"type\":\"integer\"}}," +
                   "\"required\":[\"node_type\",\"coordinates\",\"tier\"]}}}," +
                   "\"required\":[\"plan_name\",\"nodes\"]}}}]";
        }

        /// <summary>What the tool returns to the model: the verifier's verdict and the planner's route.</summary>
        public static string ToolResult(VerificationReport report)
        {
            var sb = new StringBuilder();
            if (report.Passed)
            {
                sb.Append("VERIFIED. The drone is launching on this plan. ");
                AppendRoute(sb, report);
                return sb.ToString();
            }

            sb.Append($"REJECTED: {report.Violations.Count} violation(s). Fix every one and call {ToolName} again with " +
                      "the complete corrected zone map.\n");
            foreach (var v in report.Violations) sb.Append("- ").Append(v.Message).Append('\n');
            AppendRoute(sb, report);
            return sb.ToString();
        }

        static void AppendRoute(StringBuilder sb, VerificationReport report)
        {
            var p = report.Path;
            if (p != null)
                sb.Append($"Route: {p.Lanes} survey lanes, {p.Waypoints.Count} waypoints, {p.TotalLength:0.00} m; " +
                          $"{p.Dispatches.Count} zone(s) dispatched in flight order.");
            if (report.Relay != null)
                sb.Append(report.Relay.Connected
                    ? $" Radio: every RescueLZ linked, longest chain {report.Relay.MaxHops} hop(s)."
                    : " Radio: at least one RescueLZ is isolated.");
        }

        public static string ToolArgumentsRepair(string error) =>
            $"The {ToolName} arguments could not be used: {error}. Call {ToolName} again with the complete zone map " +
            "in the required schema.";

        public static string CallTheTool =>
            $"Do not reply with text. Call {ToolName} with your complete zone map.";

        public static string ParseRepair(string error) =>
            "Your reply could not be used: " + error + ". Reply with ONLY the complete JSON object in the " +
            "required schema.";

        public static string VerificationRepair(IReadOnlyList<PlanViolation> violations)
        {
            var sb = new StringBuilder("The zone map failed verification. Fix every problem below and reply with ONLY " +
                                       "the complete corrected JSON zone map:\n");
            foreach (var v in violations) sb.Append("- ").Append(v.Message).Append('\n');
            return sb.ToString();
        }
    }
}
