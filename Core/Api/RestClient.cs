using System;
using System.Collections.Generic;
using System.Net;
using YaMus.Core.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace YaMus.Core.Api
{
    /// <summary>Тонкая обёртка над HTTP: JSON-десериализация, авторизация, базовые заголовки Yandex.</summary>
    public sealed class RestClient
    {
        public const string ApiBase = "https://api.music.yandex.net";
        public const string ClientHeaderValue = "YandexMusicDesktopAppWindows/5.66.1";

        private readonly HttpClient _http;
        private string _token;

        public RestClient(HttpClient http)
        {
            _http = http;
        }

        public string Token
        {
            get { return _token; }
            set { _token = value; }
        }

        public JToken GetJson(string path)
        {
            var response = _http.Get(ApiBase + path, BuildHeaders("GET", null));
            try
            {
                if (response.StatusCode < 200 || response.StatusCode >= 300)
                {
                    throw ApiError.From(response);
                }
                string text = response.BodyText;
                return JToken.Parse(text);
            }
            finally
            {
                response.Dispose();
            }
        }

        public JToken PostJson(string path, object body)
        {
            byte[] bytes = body == null ? new byte[0] : Encoding_Utf8.GetBytes(JsonConvert.SerializeObject(body));
            var response = _http.Post(ApiBase + path, bytes, "application/json;charset=utf-8", BuildHeaders("POST", bytes.Length));
            try
            {
                if (response.StatusCode < 200 || response.StatusCode >= 300)
                {
                    throw ApiError.From(response);
                }
                return JToken.Parse(response.BodyText);
            }
            finally
            {
                response.Dispose();
            }
        }

        /// <summary>POST без тела (лайки/разлайки — сервер не принимает тело JSON).</summary>
        public JToken PostEmpty(string path)
        {
            var response = _http.Post(ApiBase + path, new byte[0], null, BuildHeaders("POST", 0));
            try
            {
                if (response.StatusCode < 200 || response.StatusCode >= 300)
                {
                    throw ApiError.From(response);
                }
                return JToken.Parse(response.BodyText);
            }
            finally
            {
                response.Dispose();
            }
        }

        /// <summary>HTTP-запрос с необработанным телом наружу (для стримов обложек/файлов).</summary>
        public HttpResponse Request(string url)
        {
            return _http.Get(url, BuildHeaders("GET", null));
        }

        private Dictionary<string, string> BuildHeaders(string method, int? contentLength)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            headers["X-Yandex-Music-Client"] = ClientHeaderValue;
            if (!string.IsNullOrEmpty(_token))
            {
                headers["Authorization"] = "OAuth " + _token;
            }
            headers["Accept"] = "application/json";
            return headers;
        }

        private static readonly System.Text.Encoding Encoding_Utf8 = System.Text.Encoding.UTF8;
    }

    public sealed class ApiError : Exception
    {
        public int StatusCode;
        public string Body;

        public ApiError(int statusCode, string body)
            : base("HTTP " + statusCode + ": " + body)
        {
            StatusCode = statusCode;
            Body = body;
        }

        public static ApiError From(HttpResponse response)
        {
            string body = null;
            try { body = response.BodyText; }
            catch { }
            return new ApiError(response.StatusCode, body);
        }
    }
}