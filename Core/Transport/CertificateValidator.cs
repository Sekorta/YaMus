using System;
using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using YaMus.Core.Diagnostics;

namespace YaMus.Core.Transport
{
    /// <summary>
    /// Валидация сертификата по системному хранилищу + проверка hostname.
    /// Срок действия и совпадение hostname проверяются ВСЕГДА (даже при allowUntrusted).
    /// allowUntrusted=true ослабляет только построение цепочки до корня — это нужно на
    /// древних ОС (XP) без актуальных корневых сертификатов. Отключение проверки hostname
    /// открывало бы MITM, поэтому оно убрано.</summary>
    public sealed class CertificateValidator
    {
        private readonly bool _allowUntrusted;

        public CertificateValidator(bool allowUntrusted)
        {
            _allowUntrusted = allowUntrusted;
        }

        public void Validate(Org.BouncyCastle.Asn1.X509.X509CertificateStructure[] chain, string host)
        {
            if (chain == null || chain.Length == 0)
            {
                Diag.LogError("cert: сервер не прислал сертификат");
                throw new Exception("Сервер не прислал сертификат");
            }

            var certificates = new List<X509Certificate2>();
            try
            {
                foreach (var c in chain)
                {
                    certificates.Add(new X509Certificate2(c.GetEncoded()));
                }
            }
            catch (Exception ex)
            {
                Diag.LogError("cert: parse failed (" + host + "): " + ex.GetType().Name + ": " + ex.Message);
                throw new Exception("Не удалось разобрать сертификат сервера: " + ex.Message);
            }

            X509Certificate2 leaf = certificates[0];

            // Срок действия — проверяем всегда, независимо от allowUntrusted.
            DateTime now = DateTime.Now;
            if (now < leaf.NotBefore || now > leaf.NotAfter)
            {
                Diag.LogError("cert: срок действия (" + host + ") " + leaf.Subject
                              + " notBefore=" + leaf.NotBefore.ToString("o")
                              + " notAfter=" + leaf.NotAfter.ToString("o")
                              + " now=" + now.ToString("o"));
                throw new Exception("Срок действия сертификата истёк: " + leaf.Subject);
            }

            // Hostname — проверяем всегда, независимо от allowUntrusted.
            if (host != null && !IsHostMatch(leaf, host))
            {
                Diag.LogError("cert: host mismatch (" + host + "): " + leaf.Subject);
                throw new Exception("Сертификат не соответствует хосту " + host + ": " + leaf.Subject);
            }

            // Цепочку строим ТОЛЬКО там, где её реально требуем (Win10+). На XP/Win7
            // X509Chain.Build выполняется на каждый запрос, тормозит и затягивает
            // рукопожатие настолько, что сервер успевает оборвать его (alert 40 =
            // handshake_failure), а результат всё равно игнорируется. Пропускаем.
            if (!_allowUntrusted)
            {
                X509Chain chainBuilder = new X509Chain();
                chainBuilder.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chainBuilder.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chainBuilder.ChainPolicy.UrlRetrievalTimeout = TimeSpan.Zero;
                for (int i = 1; i < certificates.Count; i++)
                {
                    chainBuilder.ChainPolicy.ExtraStore.Add(certificates[i]);
                }

                bool valid = false;
                string chainStatus = "";
                try
                {
                    valid = chainBuilder.Build(leaf);
                    if (!valid) chainStatus = BuildStatusText(chainBuilder);
                }
                catch (Exception ex)
                {
                    valid = false;
                    chainStatus = ex.GetType().Name + ": " + ex.Message;
                }

                if (!valid)
                {
                    DisposeAll(certificates);
                    Diag.LogError("cert: chain untrusted (" + host + "): " + chainStatus);
                    throw new Exception("Цепочка сертификатов не доверенная для " + host);
                }
            }

            DisposeAll(certificates);
        }

        private static void DisposeAll(List<X509Certificate2> certificates)
        {
            foreach (var c in certificates)
            {
                // X509Certificate2 стал IDisposable только в .NET 4.6; на 4.0 (Win7/XP)
                // каст бросал InvalidCastException. Через "as" на 4.0 просто пропускаем
                // (объект освободит финализатор), на новых рантаймах — освобождаем.
                var disposable = c as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        private static string BuildStatusText(X509Chain chain)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (X509ChainStatus st in chain.ChainStatus)
                {
                    if (sb.Length > 0) sb.Append("; ");
                    sb.Append(st.Status).Append(": ").Append(st.StatusInformation);
                }
                return sb.Length > 0 ? sb.ToString() : "unknown";
            }
            catch
            {
                return "?";
            }
        }

        private static bool IsHostMatch(X509Certificate2 cert, string host)
        {
            // X509Certificate2.GetNameInfo(DnsName) отдаёт только ПЕРВЫЙ SAN, а у Яндекс.Музыки
            // в сертификате их десяток (api.music.yandex.net/.ru/.kz/...). Поэтому разбираем
            // SAN-расширение целиком и сравниваем хост со ВСЕМИ dNSName (+ CN как запасной).
            foreach (string name in GetCertNames(cert))
            {
                if (MatchName(name, host)) return true;
            }
            return false;
        }

        private static bool MatchName(string name, string host)
        {
            if (string.IsNullOrEmpty(name)) return false;
            name = name.Trim();
            if (name.Length == 0) return false;

            if (name.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;

            // wildcard: "*.music.yandex.net" матчит "api.music.yandex.net", но не "a.b.…"
            if (name.StartsWith("*.", StringComparison.Ordinal) && host.IndexOf('.') > 0)
            {
                string rest = host.Substring(host.IndexOf('.'));
                string wildcard = name.Substring(1);
                if (rest.Equals(wildcard, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static List<string> GetCertNames(X509Certificate2 cert)
        {
            var names = new List<string>();
            try
            {
                string cn = cert.GetNameInfo(X509NameType.SimpleName, false);
                if (!string.IsNullOrEmpty(cn)) names.Add(cn);
            }
            catch { }

            try
            {
                foreach (X509Extension ext in cert.Extensions)
                {
                    if (ext.Oid != null && ext.Oid.Value == "2.5.29.17")
                    {
                        ParseSanDns(ext.RawData, names);
                    }
                }
            }
            catch { }
            return names;
        }

        /// <summary>Разбирает DER-содержимое SAN (SEQUENCE of GeneralName) и собирает
        /// все записи dNSName (тег контекста [2], 0x82).</summary>
        private static void ParseSanDns(byte[] data, List<string> into)
        {
            if (data == null || data.Length < 2 || data[0] != 0x30) return;
            int pos = 1;
            int len = ReadDerLength(data, ref pos);
            int end = pos + len;
            if (end > data.Length) end = data.Length;
            while (pos < end)
            {
                int tag = data[pos++];
                int glen = ReadDerLength(data, ref pos);
                if (pos + glen > data.Length) break;
                if (tag == 0x82)
                {
                    into.Add(Encoding.ASCII.GetString(data, pos, glen));
                }
                pos += glen;
            }
        }

        private static int ReadDerLength(byte[] data, ref int pos)
        {
            int b = data[pos++];
            if ((b & 0x80) == 0) return b;
            int n = b & 0x7F;
            int len = 0;
            for (int i = 0; i < n && pos < data.Length; i++)
            {
                len = (len << 8) | data[pos++];
            }
            return len;
        }
    }
}