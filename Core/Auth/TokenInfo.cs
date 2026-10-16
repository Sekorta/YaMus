namespace YaMus.Core.Auth
{
    public sealed class TokenInfo
    {
        public string OAuthToken;
        /// <summary>Тот же токен, но зашифрованный через DPAPI (CurrentUser) и в base64.
        /// В файле на диске хранится только это поле; OAuthToken живёт лишь в памяти.
        /// Старые конфиги с открытым OAuthToken читаются по-прежнему (миграция при Save).</summary>
        public string TokenProtected;
        public long Uid;
        public string Login;
        public string DeviceId;
        public string Theme;
        public bool? ThemeDark;
        public string AudioQuality;
    }
}