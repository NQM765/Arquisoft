using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class ApiClient : MonoBehaviour
{
    [Header("Support API")]
    [SerializeField] string baseUrl = "https://127.0.0.1:8000";
    [SerializeField] string sessionSummaryEndpoint = "/support/session-summary";

    [Header("Web Login")]
    [SerializeField] string webLoginUrl = "http://localhost:3000/unity-login";
    [SerializeField] int callbackPort;
    [SerializeField] float webLoginTimeoutSeconds = 120f;
    [SerializeField] bool forceGoogleAccountSelection = true;

    public static ApiClient Instance { get; private set; }
    public bool IsAuthenticated => AuthSession.IsAuthenticated;
    public int UserId => AuthSession.UserId;
    public string Username => AuthSession.Username;
    public string AccessToken => AuthSession.AccessToken;

    bool isLoggedIn;
    bool loginInProgress;

    [Serializable]
    public class LoginResponse
    {
        public string message;
        public int user_id;
        public string username;
        public string access_token;
        public string token_type;
    }

    class CallbackResult
    {
        public bool success;
        public string error;
        public LoginResponse response;
    }

    public static ApiClient GetOrCreate()
    {
        if (Instance != null)
        {
            return Instance;
        }

        Instance = FindFirstObjectByType<ApiClient>();
        if (Instance != null)
        {
            return Instance;
        }

        GameObject go = new GameObject("ApiClient");
        Instance = go.AddComponent<ApiClient>();
        return Instance;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void Login(string username, string password, Action<LoginResponse> onSuccess, Action<string> onError)
    {
        LoginWithBrowser(onSuccess, onError);
    }

    public void LoginWithBrowser(Action<LoginResponse> onSuccess, Action<string> onError)
    {
        if (loginInProgress)
        {
            onError?.Invoke("Ya hay un login en curso.");
            return;
        }

        StartCoroutine(WebLoginCoroutine(onSuccess, onError));
    }

    public void SendSessionSummary(GameSessionStats.SessionSummaryPayload payload, Action<string> onSuccess, Action<string> onError)
    {
        if (payload == null)
        {
            onError?.Invoke("Payload de sesion vacio.");
            return;
        }

        if (!IsAuthenticated)
        {
            onError?.Invoke("No autenticado.");
            return;
        }

        StartCoroutine(PostJsonCoroutine(sessionSummaryEndpoint, payload,
            onSuccess: text =>
            {
                string message = "Sesion enviada.";
                if (!string.IsNullOrEmpty(text))
                {
                    message = text;
                }

                onSuccess?.Invoke(message);
            },
            onError: err =>
            {
                onError?.Invoke(err);
            }));
    }

    IEnumerator WebLoginCoroutine(Action<LoginResponse> onSuccess, Action<string> onError)
    {
        loginInProgress = true;
        TcpListener listener = null;
        Task<CallbackResult> callbackTask = null;

        try
        {
            int port = callbackPort > 0 ? callbackPort : GetAvailableLoopbackPort();
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            string state = Guid.NewGuid().ToString("N");
            string callbackUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/auth-callback/";
            string loginUrl = BuildWebLoginUrl(callbackUrl, state);

            callbackTask = Task.Run(() => WaitForCallback(listener, state));
            Application.OpenURL(loginUrl);
        }
        catch (Exception ex)
        {
            loginInProgress = false;
            if (listener != null)
            {
                listener.Stop();
            }

            onError?.Invoke("No se pudo iniciar el login web: " + ex.Message);
            yield break;
        }

        float deadline = Time.realtimeSinceStartup + Mathf.Max(10f, webLoginTimeoutSeconds);
        while (!callbackTask.IsCompleted && Time.realtimeSinceStartup < deadline)
        {
            yield return null;
        }

        if (!callbackTask.IsCompleted)
        {
            listener.Stop();
            loginInProgress = false;
            onError?.Invoke("Tiempo agotado esperando el login web.");
            yield break;
        }

        listener.Stop();
        loginInProgress = false;

        if (callbackTask.IsFaulted)
        {
            string message = callbackTask.Exception != null
                ? callbackTask.Exception.GetBaseException().Message
                : "Error desconocido.";
            onError?.Invoke("Error recibiendo el login web: " + message);
            yield break;
        }

        CallbackResult result = callbackTask.Result;
        if (result == null || !result.success)
        {
            onError?.Invoke(result != null && !string.IsNullOrEmpty(result.error) ? result.error : "Login web fallido.");
            yield break;
        }

        LoginResponse response = result.response;
        if (response == null || string.IsNullOrEmpty(response.access_token))
        {
            isLoggedIn = false;
            onError?.Invoke("Login sin token de acceso.");
            yield break;
        }

        isLoggedIn = true;
        if (string.IsNullOrEmpty(response.message))
        {
            response.message = "Login correcto.";
        }

        AuthSession.SetAuthenticated(response.user_id, response.username, response.access_token);
        onSuccess?.Invoke(response);
    }

    public void Logout()
    {
        isLoggedIn = false;
        AuthSession.Clear();
    }

    string BuildUrl(string endpoint)
    {
        string cleanBase = baseUrl.TrimEnd('/');
        string cleanEndpoint = endpoint.StartsWith("/") ? endpoint : "/" + endpoint;
        if (cleanBase.EndsWith("/support", StringComparison.OrdinalIgnoreCase)
            && cleanEndpoint.StartsWith("/support/", StringComparison.OrdinalIgnoreCase))
        {
            cleanEndpoint = cleanEndpoint.Substring("/support".Length);
        }
        return cleanBase + cleanEndpoint;
    }

    void ConfigureLocalCertificate(UnityWebRequest request)
    {
        Uri uri;
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host == "127.0.0.1" || uri.Host == "localhost"))
        {
            request.certificateHandler = new LocalhostCertificateHandler();
        }
    }

    string BuildWebLoginUrl(string callbackUrl, string state)
    {
        string separator = webLoginUrl.Contains("?") ? "&" : "?";
        return webLoginUrl
            + separator
            + "redirect_uri=" + UnityWebRequest.EscapeURL(callbackUrl)
            + "&state=" + UnityWebRequest.EscapeURL(state)
            + "&select_account=" + (forceGoogleAccountSelection ? "1" : "0");
    }

    static int GetAvailableLoopbackPort()
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    static CallbackResult WaitForCallback(TcpListener listener, string expectedState)
    {
        using (TcpClient client = listener.AcceptTcpClient())
        {
            client.ReceiveTimeout = 10000;
            client.SendTimeout = 10000;

            using (NetworkStream stream = client.GetStream())
            {
                CallbackResult result = ReadCallbackRequest(stream, expectedState);
                WriteCallbackResponse(stream, result.success, result.error);
                return result;
            }
        }
    }

    static CallbackResult ReadCallbackRequest(NetworkStream stream, string expectedState)
    {
        StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        string requestLine = reader.ReadLine();

        if (string.IsNullOrEmpty(requestLine))
        {
            return new CallbackResult { success = false, error = "Callback vacio." };
        }

        string headerLine;
        do
        {
            headerLine = reader.ReadLine();
        }
        while (!string.IsNullOrEmpty(headerLine));

        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            return new CallbackResult { success = false, error = "Callback invalido." };
        }

        Uri uri;
        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out uri))
        {
            uri = new Uri("http://127.0.0.1" + parts[1]);
        }

        Dictionary<string, string> query = ParseQuery(uri.Query);

        string returnedState = GetQueryValue(query, "state");
        if (string.IsNullOrEmpty(returnedState) || returnedState != expectedState)
        {
            return new CallbackResult { success = false, error = "Estado de login invalido." };
        }

        string error = GetQueryValue(query, "error");
        if (!string.IsNullOrEmpty(error))
        {
            return new CallbackResult { success = false, error = error };
        }

        string accessToken = GetQueryValue(query, "access_token");
        if (string.IsNullOrEmpty(accessToken))
        {
            return new CallbackResult { success = false, error = "La web no devolvio token de acceso." };
        }

        int userId = 0;
        int.TryParse(GetQueryValue(query, "user_id"), NumberStyles.Integer, CultureInfo.InvariantCulture, out userId);

        LoginResponse response = new LoginResponse
        {
            message = "Login correcto.",
            user_id = userId,
            username = GetQueryValue(query, "username"),
            access_token = accessToken,
            token_type = GetQueryValue(query, "token_type")
        };

        return new CallbackResult { success = true, response = response };
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> values = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(query))
        {
            return values;
        }

        string trimmed = query.StartsWith("?") ? query.Substring(1) : query;
        string[] pairs = trimmed.Split('&');
        for (int i = 0; i < pairs.Length; i++)
        {
            if (string.IsNullOrEmpty(pairs[i]))
            {
                continue;
            }

            string[] keyValue = pairs[i].Split(new[] { '=' }, 2);
            string key = Uri.UnescapeDataString(keyValue[0].Replace("+", " "));
            string value = keyValue.Length > 1 ? Uri.UnescapeDataString(keyValue[1].Replace("+", " ")) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    static string GetQueryValue(Dictionary<string, string> query, string key)
    {
        string value;
        return query.TryGetValue(key, out value) ? value : string.Empty;
    }

    static void WriteCallbackResponse(NetworkStream stream, bool success, string error)
    {
        string title = success ? "Login completo" : "Login fallido";
        string message = success
            ? "Puedes volver al juego."
            : (string.IsNullOrEmpty(error) ? "No se pudo completar el login." : error);
        string body = "<!doctype html><html><head><meta charset=\"utf-8\"><title>"
            + EscapeHtml(title)
            + "</title><script>if(window.history.replaceState){window.history.replaceState(null,'','/auth-callback/');}</script></head><body><h1>"
            + EscapeHtml(title)
            + "</h1><p>"
            + EscapeHtml(message)
            + "</p></body></html>";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers = "HTTP/1.1 200 OK\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"
            + "Connection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
    }

    static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }

    IEnumerator PostJsonCoroutine<T>(string endpoint, T payload, Action<string> onSuccess, Action<string> onError)
    {
        string json = JsonUtility.ToJson(payload);
        UnityWebRequest request = new UnityWebRequest(BuildUrl(endpoint), UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        ConfigureLocalCertificate(request);
        AuthSession.ApplyAuthorization(request);
        request.timeout = 10;

        yield return request.SendWebRequest();

        bool hasError = request.result != UnityWebRequest.Result.Success || request.responseCode >= 400;
        if (hasError)
        {
            string backendMessage = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (!string.IsNullOrEmpty(backendMessage))
            {
                onError?.Invoke(backendMessage);
            }
            else
            {
                onError?.Invoke(string.IsNullOrEmpty(request.error) ? "Request failed." : request.error);
            }

            request.Dispose();
            yield break;
        }

        string responseText = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
        onSuccess?.Invoke(responseText);
        request.Dispose();
    }

    class LocalhostCertificateHandler : CertificateHandler
    {
        protected override bool ValidateCertificate(byte[] certificateData)
        {
            return true;
        }
    }
}
