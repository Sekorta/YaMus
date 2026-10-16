using System;
using System.Collections;
using Org.BouncyCastle.Crypto.Tls;

namespace YaMus.Core.Transport
{
    internal sealed class TlsClientImpl : DefaultTlsClient
    {
        private readonly string _host;
        private readonly CertificateValidator _validator;

        public TlsClientImpl(string host, CertificateValidator validator)
        {
            _host = host;
            _validator = validator;
        }

        public override ProtocolVersion ClientVersion
        {
            get { return ProtocolVersion.TLSv12; }
        }

        public override TlsAuthentication GetAuthentication()
        {
            return new ServerAuth(_validator, _host);
        }

        public override int[] GetCipherSuites()
        {
            return new int[]
            {
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA,
                CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA,
                CipherSuite.TLS_RSA_WITH_AES_128_GCM_SHA256,
                CipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
                CipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA
            };
        }

        public override IDictionary GetClientExtensions()
        {
            IDictionary exts = base.GetClientExtensions();
            if (exts == null)
            {
                exts = new Hashtable();
            }
            ServerNameList serverNameList =
                new ServerNameList(new ServerName[] { new ServerName(NameType.host_name, _host) });
            byte[] serverNameBytes;
            using (var ms = new System.IO.MemoryStream())
            {
                serverNameList.Encode(ms);
                serverNameBytes = ms.ToArray();
            }
            exts[ExtensionType.server_name] = serverNameBytes;
            return exts;
        }

        internal sealed class ServerAuth : TlsAuthentication
        {
            private readonly CertificateValidator _validator;
            private readonly string _host;

            public ServerAuth(CertificateValidator validator, string host)
            {
                _validator = validator;
                _host = host;
            }

            public void NotifyServerCertificate(Certificate serverCertificate)
            {
                _validator.Validate(serverCertificate.GetCertificateList(), _host);
            }

            public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest)
            {
                return null;
            }
        }
    }
}