using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OmniVCamController
{
    internal sealed class ObsWebSocketClient : IDisposable
    {
        private const int OperationTimeoutMs = 5000;
        private readonly Uri uri;
        private readonly string password;
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private int requestId;

        public ObsWebSocketClient(string url, string password)
        {
            uri = new Uri(url);
            this.password = password ?? string.Empty;
        }

        public async Task ConnectAsync()
        {
            using (CancellationTokenSource timeout = CreateTimeoutToken())
            {
                try
                {
                    await socket.ConnectAsync(uri, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("OBS WebSocket connection timed out.");
                }
            }
            Dictionary<string, object> hello = await ReceiveAsync();
            Dictionary<string, object> helloData = GetDictionary(hello, "d");
            var identifyData = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
            Dictionary<string, object> authentication = GetDictionary(helloData, "authentication", false);
            if (authentication != null)
                identifyData["authentication"] = CreateAuthentication(password, Convert.ToString(authentication["salt"]), Convert.ToString(authentication["challenge"]));
            await SendAsync(new Dictionary<string, object> { ["op"] = 1, ["d"] = identifyData });
            Dictionary<string, object> identified = await ReceiveAsync();
            if (Convert.ToInt32(identified["op"]) != 2) throw new InvalidOperationException("OBS authentication failed.");
        }

        public async Task<string> GetCurrentProgramSceneAsync()
        {
            Dictionary<string, object> data = await SendRequestForDataAsync("GetCurrentProgramScene", null);
            return Convert.ToString(data["currentProgramSceneName"]);
        }

        public async Task<List<string>> GetSceneListAsync()
        {
            Dictionary<string, object> data = await SendRequestForDataAsync("GetSceneList", null);
            return GetNameList(data, "scenes", "sceneName");
        }

        public async Task<List<string>> GetSceneItemListAsync(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) throw new InvalidOperationException("OBS scene is required.");
            Dictionary<string, object> data = await SendRequestForDataAsync("GetSceneItemList",
                new Dictionary<string, object> { ["sceneName"] = sceneName });
            return GetNameList(data, "sceneItems", "sourceName");
        }

        public async Task SendRequestAsync(string requestType, IDictionary<string, object> requestData)
        {
            await SendRequestForDataAsync(requestType, requestData);
        }

        private async Task<Dictionary<string, object>> SendRequestForDataAsync(string requestType, IDictionary<string, object> requestData)
        {
            string id = (++requestId).ToString();
            await SendAsync(new Dictionary<string, object>
            {
                ["op"] = 6,
                ["d"] = new Dictionary<string, object> { ["requestType"] = requestType, ["requestId"] = id, ["requestData"] = requestData ?? new Dictionary<string, object>() }
            });
            Dictionary<string, object> response = await ReceiveAsync();
            Dictionary<string, object> data = GetDictionary(response, "d");
            Dictionary<string, object> status = GetDictionary(data, "requestStatus");
            if (!Convert.ToBoolean(status["result"])) throw new InvalidOperationException(Convert.ToString(status["comment"]));
            return GetDictionary(data, "responseData", false) ?? new Dictionary<string, object>();
        }

        public async Task SetSceneItemEnabledAsync(string sceneName, string sourceName, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) throw new InvalidOperationException("OBS scene is required for source visibility actions.");
            string id = (++requestId).ToString();
            await SendAsync(new Dictionary<string, object>
            {
                ["op"] = 6,
                ["d"] = new Dictionary<string, object>
                {
                    ["requestType"] = "GetSceneItemId", ["requestId"] = id,
                    ["requestData"] = new Dictionary<string, object> { ["sceneName"] = sceneName, ["sourceName"] = sourceName }
                }
            });
            Dictionary<string, object> response = await ReceiveAsync();
            Dictionary<string, object> data = GetDictionary(response, "d");
            Dictionary<string, object> status = GetDictionary(data, "requestStatus");
            if (!Convert.ToBoolean(status["result"])) throw new InvalidOperationException(Convert.ToString(status["comment"]));
            int sceneItemId = Convert.ToInt32(GetDictionary(data, "responseData")["sceneItemId"]);
            await SendRequestAsync("SetSceneItemEnabled", new Dictionary<string, object> { ["sceneName"] = sceneName, ["sceneItemId"] = sceneItemId, ["sceneItemEnabled"] = enabled });
        }

        private async Task SendAsync(Dictionary<string, object> value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(value));
            using (CancellationTokenSource timeout = CreateTimeoutToken())
            {
                try
                {
                    await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("OBS WebSocket send timed out.");
                }
            }
        }

        private async Task<Dictionary<string, object>> ReceiveAsync()
        {
            var bytes = new byte[65536];
            int length = 0;
            WebSocketReceiveResult result;
            using (CancellationTokenSource timeout = CreateTimeoutToken())
            {
                try
                {
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes, length, bytes.Length - length), timeout.Token);
                        if (result.MessageType == WebSocketMessageType.Close)
                            throw new InvalidOperationException("OBS WebSocket closed before sending a response.");
                        length += result.Count;
                        if (length == bytes.Length && !result.EndOfMessage) throw new InvalidOperationException("OBS response is too large.");
                    } while (!result.EndOfMessage);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    throw new TimeoutException("OBS WebSocket response timed out.");
                }
            }
            return json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes, 0, length));
        }

        private static CancellationTokenSource CreateTimeoutToken()
        {
            var timeout = new CancellationTokenSource();
            timeout.CancelAfter(OperationTimeoutMs);
            return timeout;
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key, bool required = true)
        {
            if (source != null && source.TryGetValue(key, out object value) && value is Dictionary<string, object> dictionary) return dictionary;
            if (required) throw new InvalidOperationException("Invalid OBS response: " + key);
            return null;
        }

        private static List<string> GetNameList(Dictionary<string, object> source, string listKey, string nameKey)
        {
            var names = new List<string>();
            if (source == null || !source.TryGetValue(listKey, out object value) || !(value is IEnumerable values)) return names;
            foreach (object entry in values)
            {
                var dictionary = entry as Dictionary<string, object>;
                if (dictionary == null || !dictionary.TryGetValue(nameKey, out object nameValue)) continue;
                string name = Convert.ToString(nameValue);
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
            }
            return names;
        }

        private static string CreateAuthentication(string password, string salt, string challenge)
        {
            using (SHA256 sha = SHA256.Create())
            {
                string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
            }
        }

        public void Dispose()
        {
            socket.Dispose();
        }
    }
}
