using System;
using MAAYAI.HackMatrix;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Checks every <see cref="AgenticLLM_Bridge"/> in every built scene, so a bad endpoint fails the build on the
/// desk rather than on the phone as "ConnectionError (HTTP 0)".
///
/// Fails the build: an endpoint that cannot be recovered as an absolute http(s) URL with a host.
/// Warns: an endpoint that only works after normalisation (a pasted Markdown link, quotes, invisible characters);
/// an http:// endpoint that Player Settings will refuse; an empty model; an API key serialized into the scene,
/// which ships inside the APK.
/// </summary>
sealed class LlmEndpointBuildCheck : IProcessSceneWithReport
{
    public int callbackOrder => 0;

    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (report == null) return;                     // entering Play mode, not building

        foreach (var root in scene.GetRootGameObjects())
        foreach (var bridge in root.GetComponentsInChildren<AgenticLLM_Bridge>(true))
            Check(scene, bridge, report.summary.platform);
    }

    static void Check(Scene scene, AgenticLLM_Bridge bridge, BuildTarget target)
    {
        string where = $"{scene.path} > {bridge.name}";
        string raw = bridge.RawEndpointUrl;

        if (bridge.ForceOffline)
            Debug.LogWarning($"[HackMatrix] {where}: Force Offline is on - this build will never call the model.");

        if (!LlmChatClient.TryNormalizeEndpoint(raw, out string url, out string error))
            throw new BuildFailedException($"[HackMatrix] {where}: {error}. Fix Endpoint Url on AgenticLLM_Bridge.");

        if (!string.Equals(url, raw, StringComparison.Ordinal))
            Debug.LogWarning($"[HackMatrix] {where}: Endpoint Url is not a clean URL (pasted Markdown link, quotes " +
                             $"or invisible characters). It will be used as '{url}'. Paste the plain URL to silence this.");

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            PlayerSettings.insecureHttpOption != InsecureHttpOption.AlwaysAllowed &&
            !(PlayerSettings.insecureHttpOption == InsecureHttpOption.DevelopmentOnly &&
              EditorUserBuildSettings.development))
            Debug.LogWarning($"[HackMatrix] {where}: '{url}' is plain http, which Player Settings > Allow downloads " +
                             "over HTTP will block in this build. Use https, or allow HTTP for a LAN model server.");

        if (string.IsNullOrWhiteSpace(bridge.Model))
            Debug.LogWarning($"[HackMatrix] {where}: Model is empty; the endpoint will reject the request.");

        // Never log the key itself - build logs get pasted into chats and issues.
        if (bridge.HasEmbeddedApiKey)
            Debug.LogWarning($"[HackMatrix] {where}: an API key is serialized in the scene and will ship inside this " +
                             $"{target} build, where anyone with the file can extract it. Keep the build private, " +
                             "rotate the key afterwards, and do not commit the scene with the key in it.");
        else if (target == BuildTarget.Android || target == BuildTarget.iOS)
            Debug.LogWarning($"[HackMatrix] {where}: no API key in the scene. Environment variables do not reach a " +
                             "phone, so this build will fall back to the offline plan.");
    }
}
