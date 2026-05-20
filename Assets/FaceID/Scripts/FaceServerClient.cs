// Copyright (c) NanoSight.
//
// Isolated HTTP client for the face identification server.

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace NanoSight.FaceID
{
    /// <summary>
    /// Outcome of a single identification request. This struct never represents an exception:
    /// connection errors, HTTP errors, timeouts and cancellations are all surfaced through
    /// <see cref="Success"/> + <see cref="Error"/> so callers can branch without try/catch.
    /// </summary>
    public readonly struct FaceServerResult
    {
        public readonly bool Success;
        public readonly string Name;
        public readonly float Confidence;
        public readonly string Error;
        /// <summary>
        /// Free-form multi-line text the server wants shown under the name in the floating panel.
        /// The client renders it verbatim (TMP rich-text is honoured, e.g. &lt;color=...&gt;).
        /// Each deployment composes whatever lines its DB / business logic needs — the Quest
        /// client has zero knowledge of the underlying schema.
        /// </summary>
        public readonly string InfoText;

        /// <summary>
        /// Derived: a face counts as recognised when the server returned a non-empty name.
        /// No need for a separate flag in the JSON — if you got a name, you got a hit.
        /// </summary>
        public bool Recognised => Success && !string.IsNullOrWhiteSpace(Name);

        private FaceServerResult(bool success, string name, float confidence, string error,
                                 string infoText)
        {
            Success = success;
            Name = name;
            Confidence = confidence;
            Error = error;
            InfoText = infoText;
        }

        public static FaceServerResult Ok(string name, float confidence, string infoText) =>
            new(true, name, confidence, null, infoText);

        public static FaceServerResult Fail(string error) =>
            new(false, null, 0f, error, null);
    }

    /// <summary>
    /// Uploads a cropped face image (JPEG) to the identification server and parses its
    /// { "name", "confidence" } JSON response.
    ///
    /// This type intentionally has NO Unity scene dependencies (it is not a MonoBehaviour) so it
    /// stays isolated and easy to reason about. It is marked [Serializable] and held as a field on
    /// <see cref="FaceIdentifier"/>, which makes the endpoint configurable straight from the
    /// Inspector without coupling the networking code to the scene.
    /// </summary>
    [Serializable]
    public class FaceServerClient
    {
        // Server URL is no longer serialized here — it lives as a top-level field on
        // FaceIdentifier so it's the first thing visible in the Inspector without expanding the
        // "Server Client" foldout. FaceIdentifier pushes its value via SetServerUrl() on Start.
        // Runtime edits from the AppMenu also go through SetServerUrl.
        private string m_serverUrl = string.Empty;

        [Tooltip("Name of the multipart/form-data field that carries the JPEG bytes.")]
        [SerializeField] private string m_imageFieldName = "image";

        [Tooltip("Request timeout in seconds. The request is aborted if the server does not " +
                 "answer in time. Recognition takes a few seconds on CPU — keep this generous.")]
        [SerializeField, Range(1, 60)] private int m_timeoutSeconds = 30;

        [Tooltip("Log every request and response to the Unity console (useful while wiring up the server).")]
        [SerializeField] private bool m_verboseLogging;

        /// <summary>Configured endpoint URL (read-only, for logging / diagnostics).</summary>
        public string ServerUrl => m_serverUrl;

        /// <summary>HTTP request timeout in seconds. Get/set so it can be tuned at runtime.</summary>
        public int TimeoutSeconds
        {
            get => m_timeoutSeconds;
            set => m_timeoutSeconds = Mathf.Clamp(value, 1, 60);
        }

        /// <summary>Updates the endpoint URL at runtime (e.g. from the in-VR settings menu).</summary>
        public void SetServerUrl(string url) => m_serverUrl = url ?? string.Empty;

        /// <summary>
        /// Fires an HTTP GET against the configured <see cref="ServerUrl"/> as a reachability
        /// probe and considers the server connected only if the response is exactly HTTP 200.
        /// This way the SAME endpoint URL works for both the identification POST and the GET
        /// health probe — the server-side just needs to respond 200 to GET on that path.
        /// Stricter than a "any response = up" probe so we don't false-positive against random
        /// 404 / 405 pages from unrelated services on the same host.
        /// </summary>
        public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(m_serverUrl)) return false;

            using var request = UnityWebRequest.Get(m_serverUrl);
            request.timeout = m_timeoutSeconds;
            using var cancelRegistration = cancellationToken.Register(() =>
            {
                if (request.result == UnityWebRequest.Result.InProgress)
                    request.Abort();
            });

            try
            {
                await request.SendWebRequest();
            }
            catch
            {
                return false;
            }

            return !cancellationToken.IsCancellationRequested && request.responseCode == 200;
        }

        /// <summary>
        /// Shape of the JSON body returned by the server. Field names match the JSON keys
        /// exactly (<see cref="JsonUtility"/> is case-sensitive). The contract is intentionally
        /// minimal: <c>name</c> (empty string when not recognised), <c>confidence</c>, and an
        /// opaque <c>info_text</c> the server builds however it wants.
        /// </summary>
        [Serializable]
        private class ServerResponse
        {
            public string name;
            public float confidence;
            public string info_text;
        }

        /// <summary>
        /// Uploads <paramref name="jpegBytes"/> to the server and returns the identified
        /// name + confidence. This method never throws; cancel it through
        /// <paramref name="cancellationToken"/> (e.g. when the tracked face is lost).
        /// </summary>
        public async Task<FaceServerResult> IdentifyAsync(byte[] jpegBytes, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(m_serverUrl))
                return FaceServerResult.Fail("Server URL is not configured.");
            if (jpegBytes == null || jpegBytes.Length == 0)
                return FaceServerResult.Fail("Empty image payload.");
            if (cancellationToken.IsCancellationRequested)
                return FaceServerResult.Fail("Request cancelled before it started.");

            var form = new WWWForm();
            form.AddBinaryData(m_imageFieldName, jpegBytes, "face.jpg", "image/jpeg");

            using var request = UnityWebRequest.Post(m_serverUrl, form);
            request.timeout = m_timeoutSeconds;

            // Aborting the in-flight request is how we honour the cancellation token: the
            // registration is disposed at the end of the method so it cannot leak.
            using var cancelRegistration = cancellationToken.Register(() =>
            {
                if (request.result == UnityWebRequest.Result.InProgress)
                    request.Abort();
            });

            if (m_verboseLogging)
                Debug.Log($"[FaceServerClient] POST {m_serverUrl} ({jpegBytes.Length} bytes)");

            try
            {
                await request.SendWebRequest();
            }
            catch (Exception e)
            {
                // SendWebRequest's awaiter can throw if the request is aborted mid-flight.
                return cancellationToken.IsCancellationRequested
                    ? FaceServerResult.Fail("Request cancelled.")
                    : FaceServerResult.Fail($"Request failed: {e.Message}");
            }

            if (cancellationToken.IsCancellationRequested)
                return FaceServerResult.Fail("Request cancelled.");

            if (request.result != UnityWebRequest.Result.Success)
                return FaceServerResult.Fail($"{request.result}: {request.error}");

            var json = request.downloadHandler != null ? request.downloadHandler.text : null;
            if (m_verboseLogging)
                Debug.Log($"[FaceServerClient] Response: {json}");

            if (string.IsNullOrEmpty(json))
                return FaceServerResult.Fail("Server returned an empty body.");

            try
            {
                var parsed = JsonUtility.FromJson<ServerResponse>(json);
                return FaceServerResult.Ok(parsed.name, parsed.confidence, parsed.info_text);
            }
            catch (Exception e)
            {
                return FaceServerResult.Fail($"Could not parse server response '{json}': {e.Message}");
            }
        }
    }
}
