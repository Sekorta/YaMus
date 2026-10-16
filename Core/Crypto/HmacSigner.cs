using System;
using System.Security.Cryptography;
using System.Text;

namespace YaMus.Core.Crypto
{
    /// <summary>
    /// HMAC-SHA256 подпись для get-file-info (формат: ts + trackId + quality + codecs(без разделителей) + transport).
    /// Результат — base64 без padding (=), как btoa(...).slice(0, -1) в JS моде.
    /// </summary>
    public static class HmacSigner
    {
        public static string Sign(string secretKey, long ts, string trackId, string quality,
            string[] codecs, string transport)
        {
            var data = ts.ToString() + trackId + quality + string.Concat(codecs) + transport;
            byte[] key = Encoding.UTF8.GetBytes(secretKey);
            byte[] message = Encoding.UTF8.GetBytes(data);

            using (var hmac = new HMACSHA256(key))
            {
                byte[] signature = hmac.ComputeHash(message);
                return Convert.ToBase64String(signature).TrimEnd('=');
            }
        }
    }
}