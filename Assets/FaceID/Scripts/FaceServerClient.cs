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

        private FaceServerResult(bool success, string name, float confidence, string error)
        {
            Success = success;
            Name = name;
            Confidence = confidence;
            Error = error;
        }

        public static FaceServerResult Ok(string name, float confidence) => new(true, name, confidence, null);
        public static FaceServerResult Fail(string error) => new(false, null, 0f, error);
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
        [Tooltip("Full URL of the identification endpoint, e.g. http://192.168.1.50:8000/identify")]
        [SerializeField] private string m_serverUrl = "http://127.0.0.1:8000/identify";

        [Tooltip("Name of the multipart/form-data field that carries the JPEG bytes.")]
        [SerializeField] private string m_imageFieldName = "image";

        [Tooltip("Request timeout in seconds. The request is aborted if the server does not answer in time.")]
        [SerializeField, Range(1, 30)] private int m_timeoutSeconds = 5;

        [Tooltip("Log every request and response to the Unity console (useful while wiring up the server).")]
        [SerializeField] private bool m_verboseLogging;

        /// <summary>Configured endpoint URL (read-only, for logging / diagnostics).</summary>
        public string ServerUrl => m_serverUrl;

        /// <summary>
        /// Shape of the JSON body returned by the server. Field names must match the JSON keys
        /// exactly because <see cref="JsonUtility"/> is case-sensitive: { "name": "...", "confidence": 0.97 }.
        /// </summary>
        [Serializable]
        private struct ServerResponse
        {
            public string name;
            public float confidence;
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
                return FaceServerResult.Ok(parsed.name, parsed.confidence);
            }
            catch (Exception e)
            {
                return FaceServerResult.Fail($"Could not parse server response '{json}': {e.Message}");
            }
        }
    }
}
